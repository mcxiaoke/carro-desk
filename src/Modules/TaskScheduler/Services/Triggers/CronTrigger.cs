using System;
using System.Windows.Threading;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks.Triggers
{
    /// <summary>
    /// Cron 触发器。计算下一次触发时刻并做一次性精确定时，触发后立即安排下一次。
    ///
    /// 已知限制：基于本地墙钟（DateTime.Now）。夏令时「春季前跳」会跳过不存在的
    /// 那一小时内的触发点；「秋季回拨」的重复小时只触发一次。
    ///
    /// lastFiredMinute 改由服务层持有的状态仓库保管（附录 B P0）：触发器实例每次 Apply
    /// 都会重建，放实例字段会让同一分钟的补触发判定在重载后失效。
    /// </summary>
    public class CronTrigger : ITrigger
    {
        private const string LastMinuteKey = "cron:lastMinute";

        /// <summary>DispatcherTimer 的 Interval 上限约为 int.MaxValue 毫秒（约 24.8 天）。</summary>
        private static readonly TimeSpan MaxTimerInterval = TimeSpan.FromMilliseconds(int.MaxValue - 1000);

        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;

        private DispatcherTimer _timer;
        private string _expr;
        private readonly ITriggerStateStore _state;

        private DateTime LastFiredMinute
        {
            get { return _state.Get(Task.Name, LastMinuteKey) ?? DateTime.MinValue; }
            set { _state.Set(Task.Name, LastMinuteKey, value); }
        }

        /// <summary>不可达表达式只告警一次：ScheduleNext 每次重排都会走到这个分支。</summary>
        private bool _warnedUnreachable;

        /// <summary>当前定时器所指向的目标触发时刻，用于休眠恢复后的漏触发补偿。</summary>
        private DateTime? _scheduledFor;

        public DateTime? NextFireAt
        {
            get { return _scheduledFor; }
        }

        public CronTrigger(TaskDefinition task, ITriggerStateStore stateStore = null)
        {
            Task = task;
            _expr = task.Trigger.Expr ?? "";
            _state = stateStore ?? new InMemoryTriggerStateStore();
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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var next = CronHelper.GetNextOccurrence(_expr, now);
            sw.Stop();
            if (sw.ElapsedMilliseconds > 100)
            {
                try
                {
                    TaskLogger.Warn(Task != null ? Task.Name : "cron",
                        "cron next-occurrence computation took " + sw.ElapsedMilliseconds + "ms");
                }
                catch { }
            }
            if (!next.HasValue)
            {
                // 表达式合法但未来八年内没有任何触发点（例如 2 月 30 日）：不再空转，但必须留痕
                if (!_warnedUnreachable)
                {
                    _warnedUnreachable = true;
                    try
                    {
                        TaskLogger.Warn(Task != null ? Task.Name : "cron",
                            "cron expression '" + _expr + "' is valid but has no occurrence within 8 years; trigger will never fire");
                    }
                    catch { }
                }
                return;
            }

            _scheduledFor = next.Value;

            var delay = next.Value - now;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
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
            bool missed = scheduled.HasValue && minute > scheduled.Value;
            var firedMinute = missed ? scheduled.Value : minute;

            if (LastFiredMinute != firedMinute)
            {
                try
                {
                    if (missed || CronHelper.IsMatch(now, _expr))
                    {
                        LastFiredMinute = firedMinute;
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

        /// <summary>休眠/挂起恢复后的漏触发补偿。</summary>
        public void CheckCatchUp()
        {
            var scheduled = _scheduledFor;
            Stop();

            if (scheduled.HasValue && scheduled.Value <= DateTime.Now &&
                (LastFiredMinute == DateTime.MinValue || LastFiredMinute < scheduled.Value))
            {
                LastFiredMinute = new DateTime(
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
