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
    /// <summary>
    /// 一次任务运行的结构化结果。ExitCode 单独保留 int 返回值兼容旧调用方，
    /// TimedOut/Cancelled 用于区分 "-1 是退出码还是被终止"。
    /// </summary>
    public sealed class TaskRunOutcome
    {
        public int ExitCode { get; internal set; } = -1;
        public bool TimedOut { get; internal set; }
        public bool Cancelled { get; internal set; }
    }

    /// <summary>
    /// 任务进程启动与执行。
    ///
    /// 运行进程统一封装为 <see cref="TaskProcessHandle"/>（进程 + 作业对象 + 等待/停止）：
    ///   - wait 模式：<see cref="RunAsync"/> 等待退出并回传退出码（默认路径，现状语义）；
    ///   - detach 模式：<see cref="StartDetached"/> 启动即返回，调用方通过句柄观察退出/停止。
    /// 进程树回收与 killWithHost 语义见 TaskProcessHandle。
    /// </summary>
    public static class TaskRunner
    {
        public static Task<int> RunAsync(TaskDefinition task, string reason)
        {
            return RunAsync(task, reason, CancellationToken.None, null);
        }

        public static Task<int> RunAsync(TaskDefinition task, string reason, CancellationToken cancellationToken)
        {
            return RunAsync(task, reason, cancellationToken, null);
        }

        public static Task<int> RunAsync(TaskDefinition task, string reason, CancellationToken cancellationToken, TaskRunOutcome outcome)
        {
            return RunAsync(task, reason, cancellationToken, outcome, null);
        }

        /// <summary>
        /// wait 模式运行：启动进程、等待退出、回传退出码。
        /// onStarted 在进程成功启动后（等待前）回调一次，供调度器把句柄登记进运行注册表。
        /// </summary>
        public static async Task<int> RunAsync(TaskDefinition task, string reason, CancellationToken cancellationToken, TaskRunOutcome outcome, Action<TaskProcessHandle> onStarted)
        {
            if (outcome != null)
            {
                outcome.ExitCode = -1;
                outcome.TimedOut = false;
                outcome.Cancelled = false;
            }
            if (task == null) return -1;

            TaskProcessHandle handle;
            if (!TryStart(task, reason, out handle)) return -1;
            try
            {
                if (onStarted != null) onStarted(handle);
                return await handle.WaitAsync(cancellationToken, outcome).ConfigureAwait(false);
            }
            finally
            {
                handle.Dispose();
            }
        }

        /// <summary>
        /// detach 模式启动：启动进程后立即返回（0=成功），进程在后台常驻。
        /// 调用方持有句柄：先订阅 <see cref="TaskProcessHandle.Exited"/> 再调用
        /// <see cref="TaskProcessHandle.BeginExitWatch"/>（顺序不能反，否则快速退出的实例会错过事件），
        /// 停止用 <see cref="TaskProcessHandle.Stop"/>。启动失败返回 -1（句柄为 null，原因已记任务日志）。
        /// </summary>
        public static int StartDetached(TaskDefinition task, string reason, out TaskProcessHandle handle)
        {
            handle = null;
            if (task == null) return -1;
            if (!TryStart(task, reason, out handle)) return -1;
            return 0;
        }

        /// <summary>
        /// 展开变量、解析脚本与解释器包装、启动进程并挂上作业对象。
        /// 成功返回 true 且 handle 非 null；失败返回 false（原因已记任务日志，handle 为 null）。
        /// </summary>
        private static bool TryStart(TaskDefinition task, string reason, out TaskProcessHandle handle)
        {
            handle = null;
            string workDir = task.EffectiveWorkDir();
            string file = task.Action != null ? task.Action.File : "";
            string args = task.Action != null ? task.Action.Args : "";
            bool hidden = task.Options != null ? task.Options.Hidden : true;

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
                return false;
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

            TaskLogger.Info(task.Name, "triggered(" + reason + ") -> starting: " + psi.FileName + " " + psi.Arguments + " [workDir=" + workDir + "]");

            try
            {
                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

                // 输出回调在线程池线程执行：只做逐行落日志（TaskLogger 自身保证线程安全）。
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
                    return false;
                }

                // killWithHost=true（默认）时挂 kill-on-close 作业对象：宿主退出连带回收整棵树。
                // false 时不挂作业对象——宿主退出后进程继续存活，停止走 taskkill /T /F 降级链。
                bool attachJob = task.Options == null || task.Options.KillWithHost;
                ProcessJob job = null;
                if (attachJob)
                {
                    job = ProcessJob.TryCreate();
                    if (job != null && !job.TryAssign(proc))
                    {
                        job.Dispose();
                        job = null;
                    }
                }

                handle = new TaskProcessHandle(task, proc, job);
                TaskLogger.Info(task.Name, "started pid=" + handle.Pid + (job == null ? " [detached from host]" : ""));
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                return true;
            }
            catch (Exception ex)
            {
                TaskLogger.Error(task.Name, "exception: " + ex);
                handle = null;
                return false;
            }
        }
    }
}
