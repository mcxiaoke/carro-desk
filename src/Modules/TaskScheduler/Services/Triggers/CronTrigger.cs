using System;
using System.Windows.Threading;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks.Triggers
{
    /// <summary>
    /// Cron 触发器。
    ///
    /// 调度方式：不再用固定间隔"轮询当前这一分钟"，而是计算下一次触发时刻并做一次性精确定时，
    /// 触发后立即安排下一次。
    ///
    /// 为什么改：原实现每 30 秒判断一次 `IsMatch(DateTime.Now)`，只认"当前这一分钟"，
    /// 且没有任何补触发机制——UI 线程一旦被阻塞超过 60 秒（弹窗、编辑器、GC 卡顿），
    /// 该分钟的 Cron 任务会被永久跳过且无任何日志。
    ///
    /// 已知限制：基于本地墙钟（DateTime.Now）计算。夏令时"春季前跳"会跳过不存在的
    /// 那一小时内的触发点（属本地时间语义的固有歧义）；"秋季回拨"的重复小时只触发一次。
    /// </summary>
    public class CronTrigger : ITrigger
    {
        /// <summary>DispatcherTimer 的 Interval 上限约为 int.MaxValue 毫秒（约 24.8 天）。</summary>
        private static readonly TimeSpan MaxTimerInterval = TimeSpan.FromMilliseconds(int.MaxValue - 1000);

        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;

        private DispatcherTimer _timer;
        private string _expr;
        private DateTime _lastFiredMinute = DateTime.MinValue;

        /// <summary>当前定时器所指向的目标触发时刻，用于休眠恢复后的漏触发补偿。</summary>
        private DateTime? _scheduledFor;

        public CronTrigger(TaskDefinition task)
        {
            Task = task;
            _expr = task.Trigger.Expr ?? "";
        }

        public void Start()
        {
            Stop();
            ScheduleNext();
        }

        private void ScheduleNext()
        {
            Stop();

            var now = DateTime.Now;
            var next = CronHelper.GetNextOccurrence(_expr, now);
            if (!next.HasValue)
            {
                // 表达式合法但未来一年内没有任何触发点（例如 2 月 30 日），不再空转
                return;
            }

            _scheduledFor = next.Value;

            var delay = next.Value - now;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            // 触发点可能超过定时器上限（如每年 1 月 1 日）：先早醒，醒来再重新计算
            if (delay > MaxTimerInterval) delay = MaxTimerInterval;

            _timer = new DispatcherTimer { Interval = delay };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            var scheduled = _scheduledFor;
            Stop();

            var now = DateTime.Now;
            var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
            bool missed = scheduled.HasValue && scheduled.Value < now;
            var firedMinute = missed ? scheduled.Value : minute;

            if (_lastFiredMinute != firedMinute)
            {
                try
                {
                    // 正常到点仍复检表达式；若 UI/Dispatcher 阻塞跨过了原定分钟，
                    // 则依据已保存的 _scheduledFor 补触发一次，不能只检查恢复后的当前分钟。
                    if (missed || CronHelper.IsMatch(now, _expr))
                    {
                        _lastFiredMinute = firedMinute;
                        var handler = Fired;
                        if (handler != null)
                            handler(Task, missed ? "cron-catchup:" + _expr : "cron:" + _expr);
                    }
                }
                catch
                {
                    // 单个订阅者异常不得中断后续调度
                }
            }

            ScheduleNext();
        }

        /// <summary>
        /// 休眠/挂起恢复后的漏触发补偿：若睡眠期间错过了原定触发点，立即补触发一次。
        /// </summary>
        public void CheckCatchUp()
        {
            var scheduled = _scheduledFor;
            Stop();

            if (scheduled.HasValue && scheduled.Value <= DateTime.Now &&
                (_lastFiredMinute == DateTime.MinValue || _lastFiredMinute < scheduled.Value))
            {
                _lastFiredMinute = new DateTime(
                    scheduled.Value.Year, scheduled.Value.Month, scheduled.Value.Day,
                    scheduled.Value.Hour, scheduled.Value.Minute, 0);

                try
                {
                    var handler = Fired;
                    if (handler != null) handler(Task, "cron-catchup:" + _expr);
                }
                catch
                {
                    // 补偿触发失败不影响后续调度
                }
            }

            ScheduleNext();
        }

        public void Stop()
        {
            var timer = _timer;
            _timer = null;
            if (timer == null) return;
            try { timer.Stop(); } catch { }
            try { timer.Tick -= OnTick; } catch { }
        }

        public void Dispose() { Stop(); }
    }
}
