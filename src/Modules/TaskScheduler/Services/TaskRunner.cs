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

        /// <summary>StartDetached 返回码：已跳过启动（单实例互斥：同任务已有实例在运行）。</summary>
        public const int StartSkippedSingleInstance = 2;

        /// <summary>
        /// detach 模式启动：启动进程后立即返回（0=成功），进程在后台常驻。
        /// 调用方持有句柄：先订阅 <see cref="TaskProcessHandle.Exited"/> 再调用
        /// <see cref="TaskProcessHandle.BeginExitWatch"/>（顺序不能反，否则快速退出的实例会错过事件），
        /// 停止用 <see cref="TaskProcessHandle.Stop"/>。
        /// 返回 0=成功；-1=启动失败（原因已记任务日志）；2=单实例跳过（options.singleInstance 且互斥体被持有）。
        /// </summary>
        public static int StartDetached(TaskDefinition task, string reason, out TaskProcessHandle handle)
        {
            handle = null;
            if (task == null) return -1;

            // 单实例互斥：必须先于进程启动获取，冲突的启动不会产生任何进程
            TaskSingleInstanceMutex singleInstance = null;
            if (task.Options != null && task.Options.SingleInstance)
            {
                if (!TaskSingleInstanceMutex.TryAcquire(task.Name, out singleInstance))
                {
                    TaskLogger.Info(task.Name, "singleInstance: already running (mutex held), skip start (" + reason + ")");
                    return StartSkippedSingleInstance;
                }
            }

            if (!TryStart(task, reason, out handle))
            {
                if (singleInstance != null) singleInstance.Dispose();
                return -1;
            }
            if (singleInstance != null) handle.AttachMutex(singleInstance);
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

            // proc/job/startedHandle 必须在 try 外声明：catch 分支要靠它们回收资源。
            // 进程在 proc.Start() 成功后即已存在，此后的任何异常（典型如进程瞬退时
            // BeginOutputReadLine 抛 InvalidOperationException）若只做 handle=null，
            // 会留下一个无人持有的僵尸进程（调用方拿到 false，没人能 Stop 它），
            // 同时 job 作为局部变量连 SafeHandle 的终结器都等不到 —— 句柄真泄漏，
            // 且 kill-on-close 兜底因为句柄永不关闭而彻底失效。
            Process proc = null;
            ProcessJob job = null;
            TaskProcessHandle startedHandle = null;
            try
            {
                proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

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
                    try { proc.Dispose(); } catch { }
                    return false;
                }

                // killWithHost=true（默认）时挂 kill-on-close 作业对象：宿主退出连带回收整棵树。
                // false 时不挂作业对象——宿主退出后进程继续存活，停止走 taskkill /T /F 降级链。
                bool attachJob = task.Options == null || task.Options.KillWithHost;
                if (attachJob)
                {
                    job = ProcessJob.TryCreate();
                    if (job != null && !job.TryAssign(proc))
                    {
                        job.Dispose();
                        job = null;
                    }
                    if (job == null)
                    {
                        // 配置为 killWithHost=true 但作业对象不可用：退化为 taskkill 兜底，
                        // 宿主退出清理会按用户意图（KillWithHostRequested）终止进程树。
                        TaskLogger.Warn(task.Name, "killWithHost=true but job object unavailable; host exit will fall back to taskkill /T /F");
                    }
                }

                startedHandle = new TaskProcessHandle(task, proc, job);
                handle = startedHandle;
                TaskLogger.Info(task.Name, "started pid=" + handle.Pid + (job == null ? " [detached from host]" : ""));
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                return true;
            }
            catch (Exception ex)
            {
                TaskLogger.Error(task.Name, "exception: " + ex);
                handle = null;
                // 回收已启动的进程与句柄，绝不把资源留给 GC（详见上方注释）
                try
                {
                    if (startedHandle != null)
                    {
                        // 句柄已接管：由它停止整棵树并释放 job/互斥体
                        startedHandle.Stop();
                        startedHandle.Dispose();
                    }
                    else
                    {
                        // 句柄尚未接管：手动收尾。job.Dispose() 在 kill-on-close 已生效时
                        // 同样会连带终止进程树，其余情况用 Kill 兜底。
                        try { if (job != null) job.Dispose(); } catch { }
                        if (proc != null)
                        {
                            try { if (!proc.HasExited) proc.Kill(); } catch { }
                            try { proc.Dispose(); } catch { }
                        }
                    }
                }
                catch (Exception cleanupEx)
                {
                    TaskLogger.Warn(task.Name, "cleanup after start failure error: " + cleanupEx.Message);
                }
                return false;
            }
        }
    }
}
