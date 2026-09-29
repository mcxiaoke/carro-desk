using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CarroDesk.Common;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks
{
    /// <summary>
    /// 一次任务进程的运行句柄：进程 + 作业对象 + 等待/停止的统一封装。
    ///
    /// 生命周期语义由 killWithHost 决定：
    ///   true  -> 进程挂在 kill-on-close 作业对象上，宿主退出（或句柄 Stop/Dispose）连带终止整棵进程树；
    ///   false -> 不挂作业对象，宿主退出后进程继续存活（真正的常驻），Stop 走 taskkill /T /F 降级链。
    ///
    /// wait 模式由调用方 await <see cref="WaitAsync"/>；detach 模式调用 <see cref="BeginExitWatch"/>
    /// 后立即返回，退出通知经 <see cref="Exited"/> 事件回到调度器（清运行槽 + recent + 失败通知）。
    /// </summary>
    public sealed class TaskProcessHandle : IDisposable
    {
        private readonly TaskDefinition _task;
        private readonly Process _proc;
        private readonly ProcessJob _job;   // null = killWithHost=false（不挂作业对象）
        private readonly Stopwatch _sw;
        private int _stopped;               // 0/1：是否被主动 Stop 过（用户或宿主退出清理）
        private int _exitWatchStarted;
        private bool _exitRaised;
        private bool _disposed;

        internal TaskProcessHandle(TaskDefinition task, Process proc, ProcessJob job)
        {
            _task = task;
            _proc = proc;
            _job = job;
            _sw = Stopwatch.StartNew();
            StartedAt = DateTime.Now;
            int pid = -1;
            try { pid = proc.Id; } catch { }
            Pid = pid;
        }

        // 属性名不能用 Task：会遮蔽 System.Threading.Tasks.Task 类型名，导致类内 Task.Run 等调用编译失败
        public TaskDefinition Definition { get { return _task; } }
        public int Pid { get; private set; }
        public DateTime StartedAt { get; private set; }

        /// <summary>true = 进程挂在 kill-on-close 作业对象上（宿主生命周期绑定）。</summary>
        public bool KillWithHost { get { return _job != null; } }

        /// <summary>是否被主动停止过（用户停止 / 宿主退出清理）。用于区分"意外退出"。</summary>
        public bool WasStopped { get { return Volatile.Read(ref _stopped) != 0; } }

        public bool HasExited
        {
            get { try { return _proc.HasExited; } catch { return true; } }
        }

        /// <summary>进程退出后触发一次（仅 BeginExitWatch 模式）。exitCode 可能为 null（被杀时取不到）。</summary>
        public event Action<TaskProcessHandle, int?> Exited;

        /// <summary>
        /// 等待进程退出并回传退出码。
        /// timeoutSec&gt;0 超时或 ct 取消时终止整棵进程树并返回 -1（outcome 标注 TimedOut/Cancelled）。
        /// </summary>
        public async Task<int> WaitAsync(CancellationToken cancellationToken, TaskRunOutcome outcome)
        {
            if (outcome != null)
            {
                outcome.ExitCode = -1;
                outcome.TimedOut = false;
                outcome.Cancelled = false;
            }
            int timeoutSec = _task.Options != null ? _task.Options.TimeoutSec : 0;

            try
            {
                // 三者谁先到走谁：进程退出 / 超时 / 外部停止（取消令牌）。
                // 超时为 0 且无取消令牌时保持"无限等待到退出"语义。
                if (timeoutSec > 0 || cancellationToken.CanBeCanceled)
                {
                    using (var stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        if (timeoutSec > 0) stopCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                        var waitTask = Task.Run(() => _proc.WaitForExit());
                        var completed = await Task.WhenAny(waitTask, Task.Delay(Timeout.Infinite, stopCts.Token)).ConfigureAwait(false);

                        if (completed != waitTask)
                        {
                            // 外部令牌已取消 = 主动停止；否则是超时
                            bool cancelled = cancellationToken.IsCancellationRequested;
                            if (outcome != null)
                            {
                                outcome.Cancelled = cancelled;
                                outcome.TimedOut = !cancelled;
                            }
                            TaskLogger.Warn(_task.Name, (cancelled ? "stopped by request, killing pid=" : "timeout after " + timeoutSec + "s, killing pid=") + Pid);
                            Stop();

                            // 必须等 WaitForExit 任务真正结束，否则调用方 Dispose 时其仍挂起
                            try { await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false); } catch { }

                            TaskLogger.Error(_task.Name, (cancelled ? "stopped by request" : "killed on timeout") + ", duration=" + _sw.Elapsed);
                            return -1;
                        }

                        // 进程正常退出：结束挂起的 Delay 任务，释放其定时器注册
                        try { stopCts.Cancel(); } catch { }
                    }
                }
                else
                {
                    await Task.Run(() => _proc.WaitForExit()).ConfigureAwait(false);
                }

                // 无参 WaitForExit() 会等待异步输出回调处理完毕并 flush 输出缓冲，确保最后几行日志不丢
                try { await Task.Run(() => _proc.WaitForExit()).ConfigureAwait(false); } catch { }

                int exitCode = -1;
                try { exitCode = _proc.HasExited ? _proc.ExitCode : -1; } catch { }

                _sw.Stop();
                TaskLogger.Info(_task.Name, string.Format("finished pid={0} exitCode={1} duration={2:0.0}s", Pid, exitCode, _sw.Elapsed.TotalSeconds));
                if (exitCode != 0)
                {
                    TaskLogger.Warn(_task.Name, "non-zero exitCode=" + exitCode);
                }
                if (outcome != null) outcome.ExitCode = exitCode;
                return exitCode;
            }
            catch (Exception ex)
            {
                _sw.Stop();
                TaskLogger.Error(_task.Name, "exception: " + ex + " duration=" + _sw.Elapsed);
                return -1;
            }
        }

        /// <summary>
        /// 启动后台退出监视（detach 模式专用）：进程退出后写 finished 日志并触发 <see cref="Exited"/>。
        /// 幂等；监视在后台线程池执行，不占用任何执行槽。
        /// </summary>
        public void BeginExitWatch()
        {
            if (Interlocked.Exchange(ref _exitWatchStarted, 1) != 0) return;
            Task.Run(async () =>
            {
                int code = await WaitAsync(CancellationToken.None, null).ConfigureAwait(false);
                RaiseExited(code);
            });
        }

        /// <summary>
        /// 主动停止整棵进程树（用户停止 / 宿主退出清理）。
        /// killWithHost=true 时关闭作业对象即连带终止；否则走 taskkill /T /F 降级链。幂等。
        /// 即使句柄已 Dispose（如宿主退出清理顺序）也可用——taskkill 使用构造时记录的 pid。
        /// </summary>
        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            TaskLogger.Warn(_task.Name, "stopping pid=" + Pid + " (killWithHost=" + KillWithHost + ")");
            try
            {
                if (_job != null) _job.Dispose();   // kill-on-close 连带整棵树
            }
            catch { }
            try { KillTree(_proc, Pid); } catch { }
        }

        /// <summary>
        /// 释放句柄（被动）：不主动杀进程。
        /// 注意 killWithHost=true 时关闭 kill-on-close 作业对象本身就会终止仍在运行的进程树——这正是该选项的语义。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_proc != null) _proc.Dispose(); } catch { }
            try { if (_job != null) _job.Dispose(); } catch { }
        }

        private void RaiseExited(int code)
        {
            if (_exitRaised) return;
            _exitRaised = true;
            var handler = Exited;
            if (handler == null) return;
            try { handler(this, code); }
            catch (Exception ex)
            {
                try { TaskLogger.Error(_task.Name, "exit handler error: " + ex); } catch { }
            }
        }

        private static void KillTree(Process proc, int pid)
        {
            try
            {
                // taskkill /T /F 按 pid 杀整棵树；不依赖 proc 对象（句柄 Dispose 后仍可用）
                try
                {
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
                try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
            }
            catch { }
        }
    }
}
