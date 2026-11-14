using System;
using System.Windows.Threading;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks.Triggers
{
    public class DailyTrigger : ITrigger
    {
        private const string LastDateKey = "daily:lastDate";

        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;
        private DispatcherTimer _timer;
        private TimeSpan _at;

        /// <summary>
        /// 当天是否已触发改由服务层持有的状态仓库保管：触发器实例每次 Apply 都会重建，
        /// 放实例字段会被清零，重载后同一任务当天可能触发两次（附录 B P0）。
        /// </summary>
        private readonly ITriggerStateStore _state;

        public DailyTrigger(TaskDefinition task, ITriggerStateStore stateStore = null)
        {
            Task = task;
            _state = stateStore ?? new InMemoryTriggerStateStore();
            TimeSpan t;
            if (!TaskDefinition.TryParseTime(task.Trigger.At, out t))
                t = new TimeSpan(2, 0, 0);
            _at = t;

            // 防误触初始化：启动/重载时刻已晚于当天触发点时记为「今天已触发」。
            // 仅在仓库尚无记录时写入，避免每次重建都覆盖跨 Apply 保留下来的状态。
            // CatchUpMissed 的补跑由 TaskSchedulerService 在 Apply 时按会话集合投递一次（M3）。
            var now = DateTime.Now;
            if (!_state.Get(Task.Name, LastDateKey).HasValue && now >= now.Date + _at)
            {
                _state.Set(Task.Name, LastDateKey, now.Date);
            }
        }

        private DateTime LastFiredDate
        {
            get { return _state.Get(Task.Name, LastDateKey) ?? DateTime.MinValue; }
            set { _state.Set(Task.Name, LastDateKey, value); }
        }

        public DateTime? NextFireAt
        {
            get
            {
                var now = DateTime.Now;
                var todayAt = now.Date + _at;
                return todayAt > now ? todayAt : todayAt.AddDays(1);
            }
        }

        public void Start()
        {
            Stop();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _timer.Tick += OnTick;
            _timer.Start();
            if (LastFiredDate.Date != DateTime.Today)
            {
                OnTick(null, null);
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            var now = DateTime.Now;
            var todayAt = now.Date + _at;

            // 当天首次越过触发点即触发一次，包含休眠/UI 阻塞醒来后的延迟补跑。
            // LastFiredDate 在 Fired 时置位：被 when 条件或并发槽拒绝也记为「今天已跑」。
            // 这是明确的设计取舍（与 Windows 任务计划程序一致）：条件不满足时不整天反复重试。
            // 旧注释写的「90s 窗口 / 至少 60s 之后」从未在代码中实现，已删除以免误导。
            if (now >= todayAt && LastFiredDate.Date != now.Date)
            {
                LastFiredDate = now.Date;
                var h = Fired;
                if (h != null) h(Task, "daily:" + _at);
            }
        }

        /// <summary>休眠恢复后由服务层调用，补当天漏掉的触发点。</summary>
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
