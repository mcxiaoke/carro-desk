namespace CarroDesk.Modules.TaskScheduler.Models
{
    public class TaskSchedulerConfig
    {
        public bool GlobalEnabled { get; set; } = true;
        public string TasksFile { get; set; } = "tasks.json";

        public TaskSchedulerConfig Clone()
        {
            return new TaskSchedulerConfig
            {
                GlobalEnabled = GlobalEnabled,
                TasksFile = TasksFile
            };
        }
    }
}
