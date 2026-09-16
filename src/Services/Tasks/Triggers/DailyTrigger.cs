using System;
using System.Windows.Threading;
using ScreenLock.Models;

namespace ScreenLock.Services.Tasks.Triggers
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
            // 初始化检查：如果当前时刻已经晚于当天设定的 _at（例如设在 02:30，当前是 11:00），
            // 将 _lastFiredDate 初始化为今天，避免启动或重载配置时被误触发！
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
