using System;
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
        private bool _fired;

        public IdleTrigger(TaskDefinition task, IIdleService idle)
        {
            Task = task;
            _idle = idle;
        }

        public void Start()
        {
            if (_subscribed) return;
            if (_idle == null) return;
            _fired = false;
            _idle.IdleTick += OnIdleTick;
            _idle.UserActiveDetected += OnUserActive;
            _subscribed = true;
        }

        private void OnUserActive()
        {
            _fired = false;
        }

        private void OnIdleTick(TimeSpan rawIdle)
        {
            if (_fired) return;
            int afterMinutes = Task.Trigger.AfterMinutes;
            if (afterMinutes <= 0) return;
            if (rawIdle.TotalMinutes >= afterMinutes)
            {
                _fired = true;
                var h = Fired;
                if (h != null) h(Task, "idle:" + afterMinutes + "m");
            }
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