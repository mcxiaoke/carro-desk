using System;
using System.Collections.Generic;

namespace CarroDesk.Core
{
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

        /// <summary>停止该任务的运行实例（终止整棵进程树）。无运行实例返回 false。</summary>
        bool TryStop(string taskName);
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
