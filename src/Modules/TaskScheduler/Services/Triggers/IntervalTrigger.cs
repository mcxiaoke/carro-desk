using System;
using System.Windows.Threading;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks.Triggers
{
    /// <summary>
    /// 周期触发器：基于**绝对的下次触发时刻**调度，而非「从 Start() 起算一个固定间隔」。
    ///
    /// 旧实现每次 Apply 重建都会重置相位（保存一次任务就把「每 1 小时」再推迟 1 小时）。
    /// 现在 nextFire 由服务层持有的状态仓库保管，跨配置重载与宿主重启都保持相位；
    /// 因休眠/UI 阻塞错过多个周期时只补触发一次并对齐到未来，不连环补发。
    /// </summary>
    public class IntervalTrigger : ITrigger
    {
        private const string NextFireKey = "interval:nextFire";

        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;
        private DispatcherTimer _timer;
        private readonly int _intervalSec;
        private readonly ITriggerStateStore _state;
        private DateTime _nextFire;

        public IntervalTrigger(TaskDefinition task, ITriggerStateStore stateStore = null)
        {
            Task = task;
            _state = stateStore ?? new InMemoryTriggerStateStore();
            _intervalSec = task.GetIntervalSeconds();
            if (_intervalSec <= 0) _intervalSec = 60;

            var saved = _state.Get(Task.Name, NextFireKey);
            _nextFire = saved ?? DateTime.Now.AddSeconds(_intervalSec);
            if (!saved.HasValue) PersistNext();
        }

        public DateTime? NextFireAt
        {
            get { return _nextFire; }
        }

        private void PersistNext()
        {
            _state.Set(Task.Name, NextFireKey, _nextFire);
        }

        public void Start()
        {
            Stop();
            ScheduleNext();
        }

        private void ScheduleNext()
        {
            Stop();
            var delay = _nextFire - DateTime.Now;
            double ms = delay.TotalMilliseconds;
            if (ms < 1) ms = 1;   // 已过期则立即补一次；Interval 为 0 时 DispatcherTimer 不会 Tick
            if (ms > int.MaxValue - 1000) ms = int.MaxValue - 1000;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            Stop();
            var now = DateTime.Now;
            if (_nextFire <= now)
            {
                var h = Fired;
                if (h != null) h(Task, "interval:" + _intervalSec + "s");

                // 错过多个周期只补一次：以当前时刻重新对齐，避免一次性补发 N 次
                _nextFire = now.AddSeconds(_intervalSec);
                PersistNext();
            }
            ScheduleNext();
        }

        /// <summary>休眠恢复后由服务层调用，补一次漏掉的周期并重新对齐。</summary>
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
