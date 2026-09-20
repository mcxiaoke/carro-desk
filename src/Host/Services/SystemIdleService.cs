using System;
using System.Runtime.InteropServices;
using System.Threading;
using CarroDesk.Core;

namespace CarroDesk.Host.Services
{
    /// <summary>
    /// 纯物理闲时服务实现（规范 §3.4）。
    /// 唯一 1s 线程池 Timer 扇出 IdleTick；SystemBusy 缓存轮询 2s，变化才广播。
    /// 首个 IdleTick/UserActiveDetected/SystemBusyChanged 订阅者出现时懒启动，最后一个取消后自动停。
    /// </summary>
    public class SystemIdleService : IIdleService, IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int pqsns);

        public const int QUNS_NOT_PRESENT = 1;
        public const int QUNS_BUSY = 2;
        public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
        public const int QUNS_PRESENTATION_MODE = 4;
        public const int QUNS_ACCEPTS_NOTIFICATIONS = 5;
        public const int QUNS_QUIET_TIME = 6;
        public const int QUNS_APP = 7;

        private readonly Timer _timer;

        /// <summary>
        /// 保护三个委托字段的订阅与读取。
        ///
        /// 各模块在自己的 OnStart 中订阅（ScreenLock / TaskScheduler / Awake 分属不同调用链），
        /// 而 `+=` / `-=` 编译为 Delegate.Combine 后整体赋值，并非原子操作：
        /// 并发订阅时后写者会覆盖前者，表现为"某个模块的处理器凭空消失"。
        /// 这里选择锁而不是无锁 CAS——订阅只发生在模块启停时（极低频），
        /// 用锁换取显而易见且易审计的正确性更划算。
        /// </summary>
        private readonly object _handlerLock = new object();

        private Action<TimeSpan> _idleTickHandlers;
        private Action _userActiveHandlers;
        private Action<bool> _systemBusyHandlers;

        // TimeSpan 是 8 字节结构体，无法标记 volatile，因此以 ticks 存放并用 Interlocked 读写，
        // 避免 32 位进程上出现撕裂读。
        private long _rawIdleTicks;

        // bool 可以标记 volatile
        private volatile bool _isSystemBusy;
        private volatile bool _disposed;
        private int _tickCount;

        public TimeSpan RawIdle
        {
            get { return TimeSpan.FromTicks(Interlocked.Read(ref _rawIdleTicks)); }
        }

        public bool IsSystemBusyCached
        {
            get { return _isSystemBusy; }
        }

        // 显式 add/remove：首个订阅者懒启动，最后一个取消自动停，无消费者不占 Timer。
        public event Action<TimeSpan> IdleTick
        {
            add { lock (_handlerLock) { _idleTickHandlers += value; } RefreshLazy(); }
            remove { lock (_handlerLock) { _idleTickHandlers -= value; } RefreshLazy(); }
        }

        public event Action UserActiveDetected
        {
            add { lock (_handlerLock) { _userActiveHandlers += value; } RefreshLazy(); }
            remove { lock (_handlerLock) { _userActiveHandlers -= value; } RefreshLazy(); }
        }

        public event Action<bool> SystemBusyChanged
        {
            add { lock (_handlerLock) { _systemBusyHandlers += value; } RefreshLazy(); }
            remove { lock (_handlerLock) { _systemBusyHandlers -= value; } RefreshLazy(); }
        }

        public SystemIdleService()
        {
            _timer = new Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            if (_disposed) return;
            _timer.Change(0, 1000);
        }

        public void Stop()
        {
            if (_disposed) return;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void RefreshLazy()
        {
            if (_disposed) return;

            bool hasConsumers;
            lock (_handlerLock)
            {
                hasConsumers = _idleTickHandlers != null || _userActiveHandlers != null || _systemBusyHandlers != null;
            }

            if (hasConsumers) Start(); else Stop();
        }

        private void Tick(object state)
        {
            if (_disposed) return;

            TimeSpan raw = GetRawIdle();
            bool wasActive = Interlocked.Read(ref _rawIdleTicks) <= 0;

            Interlocked.Exchange(ref _rawIdleTicks, raw.Ticks);

            // 在锁内取快照、在锁外调用：既保证读到完整委托链，又不持锁执行外部代码
            Action<TimeSpan> idleHandlers;
            lock (_handlerLock) { idleHandlers = _idleTickHandlers; }
            if (idleHandlers != null)
            {
                try { idleHandlers(raw); } catch { }
            }

            if (raw <= TimeSpan.FromMilliseconds(1500) && !wasActive)
            {
                Action activeHandlers;
                lock (_handlerLock) { activeHandlers = _userActiveHandlers; }
                if (activeHandlers != null)
                {
                    try { activeHandlers(); } catch { }
                }
            }

            // 每 2 次 tick（约 2 秒）轮询一次系统忙碌状态。
            // 原先用 "DateTime.UtcNow 秒数 % 2 == 0" 判断，受计时抖动影响会出现
            // 某个 2 秒窗口触发 0 次或 2 次的情况；改为按 tick 计数，节奏确定。
            if ((Interlocked.Increment(ref _tickCount) & 1) == 0)
            {
                PollBusy();
            }
        }

        private void PollBusy()
        {
            bool busy = QueryBusy();
            if (busy == _isSystemBusy) return;

            _isSystemBusy = busy;

            Action<bool> handlers;
            lock (_handlerLock) { handlers = _systemBusyHandlers; }

            if (handlers != null)
            {
                try { handlers(busy); } catch { }
            }
        }

        public static bool IsNotificationStateBusy(int state)
        {
            return state == QUNS_BUSY || state == QUNS_RUNNING_D3D_FULL_SCREEN || state == QUNS_PRESENTATION_MODE;
        }

        private static bool QueryBusy()
        {
            try
            {
                if (SHQueryUserNotificationState(out int state) == 0)
                {
                    return IsNotificationStateBusy(state);
                }
            }
            catch { }
            return false;
        }

        private static TimeSpan GetRawIdle()
        {
            try
            {
                var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
                if (GetLastInputInfo(ref info))
                {
                    uint now = (uint)Environment.TickCount;
                    uint diff = now - info.dwTime;
                    return TimeSpan.FromMilliseconds(diff);
                }
            }
            catch { }
            return TimeSpan.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            try { _timer.Dispose(); } catch { }
        }
    }
}
