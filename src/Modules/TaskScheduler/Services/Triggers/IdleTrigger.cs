using System;
using System.Threading;
using CarroDesk.Core;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks.Triggers
{
    /// <summary>
    /// 按 afterMinutes 独立判定 IIdleService 广播的 RawIdle（规范 §3.4）。
    /// 不再订阅全局 ThresholdReached，也不自建私有 IdleDetector；每次用户活跃后自动重新武装。
    /// </summary>
    public class IdleTrigger : ITrigger
    {
        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;

        private readonly IIdleService _idle;
        private bool _subscribed;

        /// <summary>
        /// 0 = 可触发，1 = 已触发。用 Interlocked 保证"判断并置位"是单次原子操作：
        /// IdleTick 与 UserActiveDetected 都在线程池线程上扇出（见 SystemIdleService），
        /// 原先 `if (_fired) return; ... _fired = true;` 是两次非原子访问，
        /// 与用户回座事件交错时可能重复触发，或长时间无法重新武装。
        /// </summary>
        private int _fired;

        public IdleTrigger(TaskDefinition task, IIdleService idle)
        {
            Task = task;
            _idle = idle;
        }

        public void Start()
        {
            if (_subscribed) return;
            if (_idle == null) return;
            Interlocked.Exchange(ref _fired, 0);
            _idle.IdleTick += OnIdleTick;
            _idle.UserActiveDetected += OnUserActive;
            _subscribed = true;
        }

        private void OnUserActive()
        {
            Interlocked.Exchange(ref _fired, 0);
        }

        private void OnIdleTick(TimeSpan rawIdle)
        {
            int afterMinutes = Task.Trigger.AfterMinutes;
            if (afterMinutes <= 0) return;
            if (rawIdle.TotalMinutes < afterMinutes) return;

            // 只有把 _fired 从 0 换成 1 的那一次才触发，天然幂等
            if (Interlocked.CompareExchange(ref _fired, 1, 0) != 0) return;

            var h = Fired;
            if (h != null) h(Task, "idle:" + afterMinutes + "m");
        }

        public void Stop()
        {
            if (!_subscribed) return;
            try
            {
                _idle.IdleTick -= OnIdleTick;
                _idle.UserActiveDetected -= OnUserActive;
            }
            catch { }
            _subscribed = false;
        }

        public void Dispose() { Stop(); }
    }
}