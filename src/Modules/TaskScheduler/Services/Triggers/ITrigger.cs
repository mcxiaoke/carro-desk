using System;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks.Triggers
{
    public interface ITrigger : IDisposable
    {
        TaskDefinition Task { get; }
        void Start();
        void Stop();
        event Action<TaskDefinition, string> Fired;

        /// <summary>
        /// 下次预计触发时刻（本地时间）。默认接口成员：不支持/非时间驱动的触发器返回 null，
        /// 无需改动其余 9 个触发器实现即可提供可观测性。
        /// </summary>
        DateTime? NextFireAt => null;
    }
}
