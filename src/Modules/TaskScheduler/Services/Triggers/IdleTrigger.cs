using System;
using System.Threading;
using CarroDesk.Core;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks.Triggers
{
    /// <summary>
    /// 按 afterMinutes 独立判定 IIdleService 广播的 RawIdle（规范 §3.4）。
    /// 不再订阅全局 ThresholdReached，也不自建私有 IdleDetector；每次用户活跃后自动重新武装。
    ///
    /// 频率限制（2026-11-14）：在置位 _fired 之前用 limiter.CanFire 做**只读**准入判定。
    /// 被拒时保持 _fired == 0，每秒一次的 IdleTick 会继续询问，冷却/额度恢复后
    /// 若用户仍处于空闲即自动补触发，无需再经历一次「回座 → 离开」。
    /// 真正的配额消耗仍由服务层 OnTriggerFired 的 TryAcquire 统一完成，因此不会重复计数。
    /// 未注入 limiter（旧构造 / 单测）时行为与改动前完全一致。
    /// </summary>
    public class IdleTrigger : ITrigger
    {
        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;

        private readonly IIdleService _idle;
        private readonly TaskFireLimiter _limiter;
        private readonly FireLimitPolicy _policy;
        private bool _subscribed;

        /// <summary>
        /// 0 = 可触发，1 = 已触发。用 Interlocked 保证"判断并置位"是单次原子操作：
        /// IdleTick 与 UserActiveDetected 都在线程池线程上扇出（见 SystemIdleService），
        /// 原先 `if (_fired) return; ... _fired = true;` 是两次非原子访问，
        /// 与用户回座事件交错时可能重复触发，或长时间无法重新武装。
        /// </summary>
        private int _fired;

        /// <summary>拒绝日志限频状态。IdleTick 每秒一次，不限频会刷爆 5MB 轮转日志。</summary>
        private readonly object _logLock = new object();
        private static readonly TimeSpan RejectLogInterval = TimeSpan.FromSeconds(60);
        private string _lastRefusal = "";
        private DateTime _lastRefusalLogAtUtc = DateTime.MinValue;

        public IdleTrigger(TaskDefinition task, IIdleService idle, TaskFireLimiter limiter = null)
        {
            Task = task;
            _idle = idle;
            _limiter = limiter;

            var opt = task != null ? task.Options : null;
            _policy = new FireLimitPolicy
            {
                MaxRunsPerDay = opt != null ? opt.MaxRunsPerDay : 0,
                MinIntervalSec = opt != null ? opt.MinIntervalSec : 0
            };
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
            if (Volatile.Read(ref _fired) != 0) return;

            // 只读准入：不消耗配额。被拒时保持 _fired == 0，下一拍继续问，
            // 冷却/额度恢复后自动补触发，且每个空闲周期仍只触发一次。
            string refusal;
            if (!CanFireNow(out refusal))
            {
                LogRejectionRateLimited(refusal);
                return;
            }

            // 只有把 _fired 从 0 换成 1 的那一次才触发，天然幂等
            if (Interlocked.CompareExchange(ref _fired, 1, 0) != 0) return;

            var h = Fired;
            if (h != null) h(Task, "idle:" + afterMinutes + "m");
        }

        private bool CanFireNow(out string refusal)
        {
            refusal = null;
            if (_limiter == null || _policy == null || _policy.IsUnlimited) return true;
            return _limiter.CanFire(Task.Name, _policy, out refusal);
        }

        private void LogRejectionRateLimited(string refusal)
        {
            if (string.IsNullOrEmpty(refusal)) return;
            try
            {
                lock (_logLock)
                {
                    DateTime now = DateTime.UtcNow;
                    if (string.Equals(refusal, _lastRefusal, StringComparison.Ordinal) &&
                        now - _lastRefusalLogAtUtc < RejectLogInterval) return;

                    _lastRefusal = refusal;
                    _lastRefusalLogAtUtc = now;
                    TaskLogger.Info(Task.Name, "idle deferred by fire limit: " + refusal);
                }
            }
            catch { }
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
