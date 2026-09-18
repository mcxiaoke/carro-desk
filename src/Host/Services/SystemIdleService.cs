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
    public class SystemIdleService : IIdleService
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

        private Action<TimeSpan> _idleTickHandlers;
        private Action _userActiveHandlers;
        private Action<bool> _systemBusyHandlers;

        public TimeSpan RawIdle { get; private set; }
        public bool IsSystemBusyCached { get; private set; }

        // 显式 add/remove：首个订阅者懒启动，最后一个取消自动停，无消费者不占 Timer。
        public event Action<TimeSpan> IdleTick
        {
            add { _idleTickHandlers += value; RefreshLazy(); }
            remove { _idleTickHandlers -= value; RefreshLazy(); }
        }
        public event Action UserActiveDetected
        {
            add { _userActiveHandlers += value; RefreshLazy(); }
            remove { _userActiveHandlers -= value; RefreshLazy(); }
        }
        public event Action<bool> SystemBusyChanged
        {
            add { _systemBusyHandlers += value; RefreshLazy(); }
            remove { _systemBusyHandlers -= value; RefreshLazy(); }
        }

        public SystemIdleService()
        {
            _timer = new Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            _timer.Change(0, 1000);
        }

        public void Stop()
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void RefreshLazy()
        {
            bool hasConsumers = _idleTickHandlers != null || _userActiveHandlers != null || _systemBusyHandlers != null;
            if (hasConsumers) Start(); else Stop();
        }

        private void Tick(object state)
        {
            TimeSpan raw = GetRawIdle();
            bool prevActive = RawIdle <= TimeSpan.Zero;
            RawIdle = raw;
            try { _idleTickHandlers?.Invoke(raw); } catch { }

            if (raw <= TimeSpan.FromMilliseconds(1500) && prevActive == false)
            {
                try { _userActiveHandlers?.Invoke(); } catch { }
            }

            // 每约 2 次 tick 轮询一次系统忙碌状态
            if ((DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond) % 2 == 0)
            {
                PollBusy();
            }
        }

        private void PollBusy()
        {
            bool busy = QueryBusy();
            if (busy != IsSystemBusyCached)
            {
                IsSystemBusyCached = busy;
                try { _systemBusyHandlers?.Invoke(busy); } catch { }
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
    }
}