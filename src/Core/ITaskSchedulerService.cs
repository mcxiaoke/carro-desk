using System;
using System.Collections.Generic;

namespace CarroDesk.Core
{
    /// <summary>
    /// 任务进程的精确运行时状态（有限状态机状态）。
    /// </summary>
    public enum TaskRuntimeState
    {
        /// <summary>⚪ 空闲：当前无进程在运行</summary>
        Idle = 0,

        /// <summary>⏳ 启动中：申请槽位或正在初始化启动进程</summary>
        Starting = 1,

        /// <summary>🟢 运行中：进程存活且正常运行</summary>
        Running = 2,

        /// <summary>🛑 正在停止：用户已请求终止，正在清理进程树</summary>
        Stopping = 3,

        /// <summary>🟡 守护等待中：异常退出后在固定延迟期内等待自动重启</summary>
        WaitingRetry = 4,

        /// <summary>🔴 熔断失败：连续异常退出次数超预算，已停止自动重启，等待用户人工干预</summary>
        MarkedFailed = 5
    }

    /// <summary>
    /// 任务运行状态的原子快照。
    /// </summary>
    public class TaskStateSnapshot
    {
        public string TaskName { get; set; } = "";
        public TaskRuntimeState State { get; set; } = TaskRuntimeState.Idle;
        public int? Pid { get; set; }
        public DateTime? StartedAt { get; set; }
        public double DurationSec => StartedAt.HasValue ? (DateTime.Now - StartedAt.Value).TotalSeconds : 0;
        public int ConsecutiveFailures { get; set; }
        public int NextRetryDelaySec { get; set; }
        public int? LastExitCode { get; set; }
        public DateTime? LastFinishedAt { get; set; }
        public string LastOutcome { get; set; } = "";
        public int ActiveInstances { get; set; }
        public bool IsRunning => State == TaskRuntimeState.Running || State == TaskRuntimeState.Starting;
    }

    /// <summary>
    /// 任务调度能力抽象。供 View/宿主读取，避免直访 TaskSchedulerMod 门面。
    /// </summary>
    public interface ITaskSchedulerService
    {
        bool IsGlobalEnabled { get; }
        bool SetGlobalEnabled(bool enabled);
        TaskReloadResult Reload();

        /// <summary>当前有实例在运行（含 wait 执行中与 detach 常驻）的任务快照。</summary>
        IReadOnlyList<TaskRunInfo> GetRunning();

        /// <summary>该任务当前是否有实例在运行（按任务名，大小写不敏感）。</summary>
        bool IsRunning(string taskName);

        /// <summary>停止该任务的运行实例（终止整棵进程树），并取消挂起中的自动重启。无运行实例返回 false。</summary>
        bool TryStop(string taskName);

        /// <summary>立即手动触发执行一次任务。</summary>
        bool RunManual(string taskName);

        /// <summary>重启该任务：先杀掉旧实例，随后立即重新触发。</summary>
        bool RestartTask(string taskName);

        /// <summary>获取指定任务的状态机快照。</summary>
        TaskStateSnapshot GetState(string taskName);

        /// <summary>获取所有任务的运行状态快照表。</summary>
        IReadOnlyDictionary<string, TaskStateSnapshot> GetAllStates();

        /// <summary>任务运行时状态跃迁事件（任务名, 新状态, 快照）。</summary>
        event Action<string, TaskRuntimeState, TaskStateSnapshot> StateChanged;

        /// <summary>detach 任务守护状态快照：连续失败计数与熔断标记。</summary>
        TaskSupervisionInfo GetSupervision(string taskName);
    }

    /// <summary>detach 任务守护状态快照。</summary>
    public class TaskSupervisionInfo
    {
        /// <summary>连续异常失败次数超预算，已停止自动重启，等待用户干预。</summary>
        public bool MarkedFailed { get; set; }

        /// <summary>当前连续异常失败计数（稳定存活或成功退出会复位）。</summary>
        public int ConsecutiveFailures { get; set; }

        public int? LastExitCode { get; set; }

        public DateTime? MarkedAt { get; set; }
    }

    /// <summary>一个运行中任务实例的状态快照。</summary>
    public class TaskRunInfo
    {
        public string Name { get; set; }
        public int Pid { get; set; }
        public DateTime StartedAt { get; set; }
        public bool KillWithHost { get; set; }

        public double DurationSec
        {
            get { return (DateTime.Now - StartedAt).TotalSeconds; }
        }
    }

    public class TaskReloadResult
    {
        public int Tasks { get; set; }
        public int Errors { get; set; }
    }
}
