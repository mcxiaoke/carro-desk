using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CarroDesk.Common;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks
{
    public static class TaskRunner
    {
        public static async Task<int> RunAsync(TaskDefinition task, string reason)
        {
            if (task == null) return -1;
            string workDir = task.EffectiveWorkDir();
            string file = task.Action != null ? task.Action.File : "";
            string args = task.Action != null ? task.Action.Args : "";
            bool hidden = task.Options != null ? task.Options.Hidden : true;
            int timeoutSec = task.Options != null ? task.Options.TimeoutSec : 0;

            // expand env vars + template vars {{date}} etc in file/args/workDir
            try
            {
                if (!string.IsNullOrEmpty(file)) file = Environment.ExpandEnvironmentVariables(file);
                if (!string.IsNullOrEmpty(args)) args = Environment.ExpandEnvironmentVariables(args);
                if (!string.IsNullOrEmpty(workDir)) workDir = Environment.ExpandEnvironmentVariables(workDir);
                // template expansion
                file = TemplateExpander.Expand(file, task);
                args = TemplateExpander.Expand(args, task);
                workDir = TemplateExpander.Expand(workDir, task);
            }
            catch { }

            // resolve script path via scripts/ subdir (portable: exe/scripts, non-portable: %AppData%/CarroDesk/scripts)
            try { ScriptResolver.EnsureScriptsDir(); } catch { }
            string resolvedFile = file;
            try
            {
                // only resolve if file looks like script or is bare name without dir
                string extCheck = "";
                try { extCheck = Path.GetExtension(file).ToLowerInvariant(); } catch { }
                bool isScript = ScriptResolver.IsScriptExtension(extCheck);
                bool hasDir = false;
                try { hasDir = !string.IsNullOrEmpty(Path.GetDirectoryName(file)); } catch { }
                bool isRooted = false;
                try { isRooted = Path.IsPathRooted(file); } catch { }
                // if bare name (no dir) or script extension without absolute path, try scripts dir
                if (!isRooted && (isScript || !hasDir))
                {
                    string r = ScriptResolver.ResolveScriptPath(file);
                    // use resolved if it points inside scripts or file exists
                    if (!string.Equals(r, file, StringComparison.OrdinalIgnoreCase) || File.Exists(r))
                        resolvedFile = r;
                }
                else if (isScript && !isRooted)
                {
                    string r = ScriptResolver.ResolveScriptPath(file);
                    if (File.Exists(r)) resolvedFile = r;
                }
                // if file is absolute but not found, keep as is for error logging
            }
            catch { }
            file = resolvedFile;

            // resolve workDir fallback
            if (string.IsNullOrWhiteSpace(workDir) && !string.IsNullOrWhiteSpace(file))
            {
                try { workDir = Path.GetDirectoryName(Path.GetFullPath(file)); } catch { workDir = ""; }
            }
            if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
            {
                TaskLogger.Error(task.Name, "workDir does not exist: " + (workDir ?? "<empty>"));
                return -1;
            }

            // script wrapping - all scripts default hidden (no black window)
            // 包装时必须做正确的引号化/转义：原先直接字符串相加，路径含空格或引号会被拆错，
            // 且 cmd 的二次解析会把参数里的 & | < > ( ) 当作命令分隔符执行。
            string ext = "";
            try { ext = Path.GetExtension(file).ToLowerInvariant(); } catch { }
            string realFile = file;
            string realArgs = args;
            if (ext == ".ps1" || ext == ".psm1")
            {
                realFile = "powershell.exe";
                realArgs = CommandLine.AppendArgs(
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + CommandLine.QuoteArgument(file),
                    args);
            }
            else if (ext == ".bat" || ext == ".cmd")
            {
                // cmd.exe 会对命令行做二次解析：参数中的 ^ & | < > ( ) 必须转义，
                // 否则会变成命令分隔符（意外命令执行）；用 /S 保证最外层引号被正确剥离。
                realFile = "cmd.exe";
                string inner = CommandLine.AppendArgs(
                    CommandLine.EscapeForCmd(CommandLine.QuoteArgument(file)),
                    CommandLine.EscapeForCmd(args));
                realArgs = "/S /c \"" + inner + "\"";
            }
            else if (ext == ".vbs")
            {
                realFile = "wscript.exe";
                realArgs = CommandLine.AppendArgs(CommandLine.QuoteArgument(file), args);
            }
            else if (ext == ".js")
            {
                string node = ScriptResolver.FindNode();
                realFile = node;
                realArgs = CommandLine.AppendArgs(CommandLine.QuoteArgument(file), args);
            }
            else if (ext == ".py" || ext == ".pyw")
            {
                string py = ScriptResolver.FindPython();
                realFile = py;
                // for py.exe launcher, -u for unbuffered
                string pyLower = (py ?? string.Empty).ToLowerInvariant();
                string prefix = pyLower.EndsWith("py.exe") ? "" : "-u ";
                realArgs = CommandLine.AppendArgs(prefix + CommandLine.QuoteArgument(file), args);
            }

            var psi = new ProcessStartInfo
            {
                FileName = realFile,
                Arguments = realArgs,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = hidden,
                WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var sw = Stopwatch.StartNew();
            TaskLogger.Info(task.Name, "triggered(" + reason + ") -> starting: " + psi.FileName + " " + psi.Arguments + " [workDir=" + workDir + "]");

            Process proc = null;
            ProcessJob job = null;
            int exitCode = -1;
            try
            {
                proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

                // 输出回调在线程池线程执行：只做逐行落日志（TaskLogger 自身保证线程安全）。
                // 不再用 StringBuilder 跨线程累积再读取——那属于数据竞态，且累积结果从未被使用。
                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    TaskLogger.WriteOutput(task.Name, "OUT", e.Data);
                };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    TaskLogger.WriteOutput(task.Name, "ERR", e.Data);
                };

                if (!proc.Start())
                {
                    TaskLogger.Error(task.Name, "failed to start process");
                    return -1;
                }

                // 加入作业对象：超时终止或宿主退出时，整棵子进程树都会被回收，不留孤儿
                job = ProcessJob.TryCreate();
                if (job != null && !job.TryAssign(proc))
                {
                    job.Dispose();
                    job = null;
                }

                int pid = -1;
                try { pid = proc.Id; } catch { }
                TaskLogger.Info(task.Name, "started pid=" + pid);

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (timeoutSec > 0)
                {
                    using (var cts = new CancellationTokenSource())
                    {
                        // 用 TimeSpan 而非 timeoutSec * 1000，避免乘法溢出为负触发 ArgumentOutOfRange
                        var timeout = TimeSpan.FromSeconds(timeoutSec);
                        var waitTask = Task.Run(() => proc.WaitForExit(), cts.Token);
                        var completed = await Task.WhenAny(waitTask, Task.Delay(timeout, cts.Token)).ConfigureAwait(false);

                        if (completed != waitTask)
                        {
                            try { cts.Cancel(); } catch { }
                            TaskLogger.Warn(task.Name, "timeout after " + timeoutSec + "s, killing pid=" + pid);

                            // 关闭作业对象即可连带终止整棵子进程树（kill-on-close）
                            if (job != null)
                            {
                                job.Dispose();
                                job = null;
                            }
                            try { KillTree(proc); } catch { }

                            // 必须等 WaitForExit 任务真正结束，否则 finally 会在其仍挂起时 Dispose 进程对象
                            try { await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false); } catch { }

                            TaskLogger.Error(task.Name, "killed on timeout, duration=" + sw.Elapsed);
                            return -1;
                        }

                        try { cts.Cancel(); } catch { }
                    }
                }
                else
                {
                    await Task.Run(() => proc.WaitForExit()).ConfigureAwait(false);
                }

                // 无参 WaitForExit() 会等待异步输出回调处理完毕并 flush 输出缓冲，
                // 确保最后几行日志不丢——原先用固定 Task.Delay(100) 属于靠运气。
                try { await Task.Run(() => proc.WaitForExit()).ConfigureAwait(false); } catch { }

                try { exitCode = proc.HasExited ? proc.ExitCode : -1; } catch { }

                sw.Stop();
                // already logged line by line, just summary
                TaskLogger.Info(task.Name, string.Format("finished pid={0} exitCode={1} duration={2:0.0}s", pid, exitCode, sw.Elapsed.TotalSeconds));
                if (exitCode != 0)
                {
                    TaskLogger.Warn(task.Name, "non-zero exitCode=" + exitCode);
                }
                return exitCode;
            }
            catch (Exception ex)
            {
                sw.Stop();
                TaskLogger.Error(task.Name, "exception: " + ex + " duration=" + sw.Elapsed);
                return -1;
            }
            finally
            {
                try { if (proc != null) proc.Dispose(); } catch { }
                if (job != null) job.Dispose();
            }
        }

        private static void KillTree(Process proc)
        {
            try
            {
                // try kill children via WMI, fallback to Kill
                int pid = proc.Id;
                try
                {
                    // kill child processes first
                    var searcherCmd = "taskkill /PID " + pid + " /T /F";
                    var psi = new ProcessStartInfo("cmd.exe", "/c " + searcherCmd)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using (var p = Process.Start(psi)) { if (p != null) p.WaitForExit(5000); }
                }
                catch { }
                try { if (!proc.HasExited) proc.Kill(); } catch { }
            }
            catch { }
        }
    }
}
