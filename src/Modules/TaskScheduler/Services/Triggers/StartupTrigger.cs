using System;
using System.Threading;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks.Triggers
{
    public class StartupTrigger : ITrigger
    {
        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;
        private CancellationTokenSource _cts;

        /// <summary>
        /// 宿主本次会话的启动时刻。由服务层注入并在整个会话内保持不变：
        /// Apply 会重建触发器，若每次都从 Start() 重新计时，delaySec=300 的任务
        /// 只要 5 分钟内发生过一次配置重载就会被无限推迟（附录 B P0）。
        /// null（旧构造/单测）时退化为「从 Start 起算」的既有语义。
        /// </summary>
        private readonly DateTime? _hostStartedAt;

        /// <summary>本次实际排定的等待时长，供单测断言「重载不重置相位」。</summary>
        internal TimeSpan LastScheduledDelay { get; private set; } = TimeSpan.Zero;

        public StartupTrigger(TaskDefinition task, DateTime? hostStartedAt = null)
        {
            Task = task;
            _hostStartedAt = hostStartedAt;
        }

        public void Start()
        {
            Stop();
            _cts = new CancellationTokenSource();
            int delay = Task.Trigger != null ? Task.Trigger.DelaySec : 5;
            delay = Math.Max(0, Math.Min(delay, 365 * 24 * 60 * 60));

            TimeSpan wait;
            if (_hostStartedAt.HasValue)
            {
                DateTime due = _hostStartedAt.Value.AddSeconds(delay);
                wait = due - DateTime.Now;
                if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            }
            else
            {
                wait = TimeSpan.FromSeconds(delay);
            }
            LastScheduledDelay = wait;

            var token = _cts.Token;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(wait, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) return;
                    var h = Fired;
                    if (h != null) h(Task, "startup");
                }
                catch (System.Threading.Tasks.TaskCanceledException) { }
                catch { }
            });
        }

        public void Stop()
        {
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_cts != null) _cts.Dispose(); } catch { }
            _cts = null;
        }

        public void Dispose() { Stop(); }
    }
}
