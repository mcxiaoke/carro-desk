using System;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks.Triggers
{
    public class ManualTrigger : ITrigger
    {
        public TaskDefinition Task { get; private set; }

        /// <summary>
        /// 手动任务由服务层 RunManual 直接派发，不经过触发器事件；原先的 TriggerManual()
        /// 全仓库无调用方（死代码）已删除。显式空访问器满足 ITrigger 契约，
        /// 同时避免「事件从未使用」的 CS0067 警告（同 FakeIdleService 的既有写法）。
        /// </summary>
        public event Action<TaskDefinition, string> Fired { add { } remove { } }

        public ManualTrigger(TaskDefinition task)
        {
            Task = task;
        }

        /// <summary>
        /// 无定时器：手动任务由服务层 RunManual 直接派发。
        /// 原先的 TriggerManual() 全仓库无调用方（死代码），已删除，避免误以为它是触发入口。
        /// </summary>
        public void Start() { }

        public void Stop() { }

        public void Dispose() { }
    }
}
