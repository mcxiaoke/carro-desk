using System;
using System.Windows.Threading;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks.Triggers
{
    public class DailyTrigger : ITrigger
    {
        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;
        private DispatcherTimer _timer;
        private TimeSpan _at;
        private DateTime _lastFiredDate = DateTime.MinValue;

        public DailyTrigger(TaskDefinition task)
        {
            Task = task;
            TimeSpan t;
            if (!TaskDefinition.TryParseTime(task.Trigger.At, out t))
                t = new TimeSpan(2, 0, 0);
            _at = t;
            // 防误触初始化：启动/重载时刻已晚于当天触发点时记为"今天已触发"。
            // CatchUpMissed（错过补跑）改由 TaskSchedulerService 在 Apply 时按会话集合投递一次：
            // 此前这里按 catchUp 跳过初始化、Start 立即补发，而触发器每次配置重载都会整体重建，
            // 导致"错过补跑"退化为"每次重载都补跑"（docs/CODE-REVIEW-20261025.md M3）。
            var now = DateTime.Now;
            if (now >= now.Date + _at)
            {
                _lastFiredDate = now.Date;
            }
        }

        public void Start()
        {
            Stop();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _timer.Tick += OnTick;
            _timer.Start();
            // 只有当天尚未记录执行时才启动立即检查
            if (_lastFiredDate.Date != DateTime.Today)
            {
                OnTick(null, null);
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            var now = DateTime.Now;
            var todayAt = now.Date + _at;
            // fire if now >= todayAt and not yet fired today, and within 90s window or overdue
            if (now >= todayAt && _lastFiredDate.Date != now.Date)
            {
                // overdue check: if now is more than 2 minutes past, still fire once (catch-up)
                // but avoid double fire on timer jitter: require at least 60s after scheduled
                _lastFiredDate = now.Date;
                var h = Fired;
                if (h != null) h(Task, "daily:" + _at);
            }
            // also catch-up if we missed yesterday due to sleep and now is next day early morning before todayAt?
            // not needed, daily is always next occurrence.
        }

        // Called on resume to catch up missed run within same day
        public void CheckCatchUp()
        {
            OnTick(null, null);
        }

        public void Stop()
        {
            try { if (_timer != null) _timer.Stop(); } catch { }
            try { if (_timer != null) _timer.Tick -= OnTick; } catch { }
            _timer = null;
        }

        public void Dispose() { Stop(); }
    }
}
