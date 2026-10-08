using System;
using CarroDesk.Core;

namespace CarroDesk.Services.Tasks
{
    /// <summary>
    /// 单个任务的有限状态机 (Finite State Machine)。
    /// 严密管理任务生命周期状态跃迁，消除散落的布尔状态与竞态隐患。
    /// 线程安全。
    /// </summary>
    public sealed class TaskStateMachine
    {
        private readonly object _lock = new object();

        public string TaskName { get; }
        public TaskRuntimeState CurrentState { get; private set; } = TaskRuntimeState.Idle;
        public int ActiveInstances { get; private set; }
        public int? CurrentPid { get; private set; }
        public DateTime? CurrentStartedAt { get; private set; }
        public int ConsecutiveFailures { get; private set; }
        public int NextRetryDelaySec { get; private set; }
        public int? LastExitCode { get; private set; }
        public DateTime? LastFinishedAt { get; private set; }
        public string LastOutcome { get; private set; } = "";
        public DateTime? MarkedFailedAt { get; private set; }

        public TaskStateMachine(string taskName)
        {
            TaskName = taskName ?? "";
        }

        public bool TryBeginStarting(bool userDriven, out string refusalReason)
        {
            return TryBeginStarting(userDriven, false, out refusalReason);
        }

        /// <summary>
        /// 尝试进入 Starting 状态。
        /// </summary>
        public bool TryBeginStarting(bool userDriven, bool allowConcurrent, out string refusalReason)
        {
            lock (_lock)
            {
                if (!allowConcurrent && (CurrentState == TaskRuntimeState.Running || CurrentState == TaskRuntimeState.Starting))
                {
                    refusalReason = "任务已在运行或启动中";
                    return false;
                }
                if (CurrentState == TaskRuntimeState.Stopping)
                {
                    refusalReason = "任务正在停止中，请稍候";
                    return false;
                }
                if (CurrentState == TaskRuntimeState.MarkedFailed)
                {
                    if (!userDriven)
                    {
                        refusalReason = $"任务连续异常失败 {ConsecutiveFailures} 次已熔断，需手动运行或重新保存复位";
                        return false;
                    }
                    // 人工驱动视为显式复位意图
                    ConsecutiveFailures = 0;
                    MarkedFailedAt = null;
                }

                CurrentState = (CurrentState == TaskRuntimeState.Running) ? TaskRuntimeState.Running : TaskRuntimeState.Starting;
                refusalReason = null;
                return true;
            }
        }

        /// <summary>
        /// 进程成功启动，跃迁到 Running 状态。
        /// </summary>
        public void OnProcessStarted(int pid, DateTime startTime)
        {
            lock (_lock)
            {
                ActiveInstances++;
                CurrentState = TaskRuntimeState.Running;
                CurrentPid = pid;
                if (!CurrentStartedAt.HasValue) CurrentStartedAt = startTime;
                LastOutcome = ActiveInstances > 1 ? $"运行中 ({ActiveInstances} 实例)" : "运行中";
            }
        }

        /// <summary>
        /// 启动失败（如可执行文件不存在或权限不足），跃迁处理。
        /// </summary>
        public TaskRuntimeState OnStartFailed(bool shouldRetry, int retryDelaySec, int retryLimit, string error)
        {
            lock (_lock)
            {
                if (ActiveInstances > 0)
                {
                    // 仍有其它实例在运行，不改变总体运行态
                    return CurrentState;
                }

                CurrentPid = null;
                CurrentStartedAt = null;
                LastExitCode = -1;
                LastFinishedAt = DateTime.Now;
                ConsecutiveFailures++;

                if (shouldRetry)
                {
                    if (ConsecutiveFailures > retryLimit)
                    {
                        CurrentState = TaskRuntimeState.MarkedFailed;
                        MarkedFailedAt = DateTime.Now;
                        LastOutcome = $"启动失败熔断: {error}";
                    }
                    else
                    {
                        CurrentState = TaskRuntimeState.WaitingRetry;
                        NextRetryDelaySec = retryDelaySec;
                        LastOutcome = $"启动失败等待重试: {error}";
                    }
                }
                else
                {
                    CurrentState = TaskRuntimeState.Idle;
                    LastOutcome = $"启动失败: {error}";
                }

                return CurrentState;
            }
        }

        /// <summary>
        /// 进程退出，计算状态跃迁。
        /// </summary>
        public TaskRuntimeState OnProcessExited(
            int exitCode,
            bool wasUserStopped,
            bool shouldRetry,
            int retryDelaySec,
            int retryLimit,
            int stableUptimeSec)
        {
            lock (_lock)
            {
                if (ActiveInstances > 0) ActiveInstances--;
                var duration = CurrentStartedAt.HasValue ? (DateTime.Now - CurrentStartedAt.Value).TotalSeconds : 0;
                LastExitCode = exitCode;
                LastFinishedAt = DateTime.Now;

                // 若仍有其它并发实例在运行，保持 Running 状态
                if (ActiveInstances > 0)
                {
                    CurrentState = TaskRuntimeState.Running;
                    LastOutcome = ActiveInstances > 1 ? $"运行中 ({ActiveInstances} 实例)" : "运行中";
                    return CurrentState;
                }

                CurrentPid = null;
                CurrentStartedAt = null;

                // 用户主动停止：绝不重试，不计失败
                if (wasUserStopped || CurrentState == TaskRuntimeState.Stopping)
                {
                    CurrentState = TaskRuntimeState.Idle;
                    LastOutcome = "手动停止";
                    NextRetryDelaySec = 0;
                    return CurrentState;
                }

                // 正常退出 (0)
                if (exitCode == 0)
                {
                    CurrentState = TaskRuntimeState.Idle;
                    ConsecutiveFailures = 0;
                    NextRetryDelaySec = 0;
                    MarkedFailedAt = null;
                    LastOutcome = "成功 (0)";
                    return CurrentState;
                }

                // 稳定存活复位（长稳运行后的偶发退出不连坐历史失败）
                if (stableUptimeSec > 0 && duration >= stableUptimeSec)
                {
                    ConsecutiveFailures = 0;
                    MarkedFailedAt = null;
                }

                ConsecutiveFailures++;

                if (shouldRetry)
                {
                    if (ConsecutiveFailures > retryLimit)
                    {
                        CurrentState = TaskRuntimeState.MarkedFailed;
                        MarkedFailedAt = DateTime.Now;
                        NextRetryDelaySec = 0;
                        LastOutcome = $"熔断 (连续退出 {ConsecutiveFailures} 次)";
                    }
                    else
                    {
                        CurrentState = TaskRuntimeState.WaitingRetry;
                        NextRetryDelaySec = retryDelaySec;
                        LastOutcome = $"异常退出 (exit={exitCode})，{retryDelaySec}s后重试#{ConsecutiveFailures}";
                    }
                }
                else
                {
                    CurrentState = TaskRuntimeState.Idle;
                    NextRetryDelaySec = 0;
                    LastOutcome = $"退出 (exit={exitCode})";
                }

                return CurrentState;
            }
        }

        /// <summary>
        /// 用户请求停止任务。
        /// 如果处于 WaitingRetry 倒计时，直接取消重试并跃迁回 Idle。
        /// 如果处于 Starting 或 Running，跃迁为 Stopping。
        /// </summary>
        public bool RequestStop(out bool cancelledPendingRetry)
        {
            lock (_lock)
            {
                cancelledPendingRetry = false;
                if (CurrentState == TaskRuntimeState.WaitingRetry)
                {
                    CurrentState = TaskRuntimeState.Idle;
                    cancelledPendingRetry = true;
                    NextRetryDelaySec = 0;
                    LastOutcome = "已取消自动重试";
                    return true;
                }
                if (CurrentState == TaskRuntimeState.Running || CurrentState == TaskRuntimeState.Starting)
                {
                    CurrentState = TaskRuntimeState.Stopping;
                    LastOutcome = "正在停止...";
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 停止清理完成，跃迁回 Idle。
        /// </summary>
        public void OnStopCompleted()
        {
            lock (_lock)
            {
                ActiveInstances = 0;
                CurrentState = TaskRuntimeState.Idle;
                CurrentPid = null;
                CurrentStartedAt = null;
                NextRetryDelaySec = 0;
                LastOutcome = "已停止";
            }
        }

        /// <summary>
        /// 人工干预重置（如重新编辑保存配置）。
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                ConsecutiveFailures = 0;
                NextRetryDelaySec = 0;
                MarkedFailedAt = null;
                if (CurrentState == TaskRuntimeState.MarkedFailed)
                {
                    CurrentState = TaskRuntimeState.Idle;
                    LastOutcome = "已重置";
                }
            }
        }

        /// <summary>
        /// 获取当前状态快照。
        /// </summary>
        public TaskStateSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                return new TaskStateSnapshot
                {
                    TaskName = TaskName,
                    State = CurrentState,
                    Pid = CurrentPid,
                    StartedAt = CurrentStartedAt,
                    ConsecutiveFailures = ConsecutiveFailures,
                    NextRetryDelaySec = NextRetryDelaySec,
                    LastExitCode = LastExitCode,
                    LastFinishedAt = LastFinishedAt,
                    LastOutcome = LastOutcome,
                    ActiveInstances = ActiveInstances
                };
            }
        }
    }
}
