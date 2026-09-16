namespace CarroDesk.Core
{
    /// <summary>
    /// 任务调度能力抽象。供 View/宿主读取，避免直访 TaskSchedulerMod 门面。
    /// </summary>
    public interface ITaskSchedulerService
    {
        bool IsGlobalEnabled { get; }
        void SetGlobalEnabled(bool enabled);
        TaskReloadResult Reload();
    }

    public class TaskReloadResult
    {
        public int Tasks { get; set; }
        public int Errors { get; set; }
    }
}