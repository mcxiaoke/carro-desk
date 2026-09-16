using System;
using System.Runtime.InteropServices;
using System.Threading;
using CarroDesk.Core;

namespace CarroDesk.Host.Services
{
    /// <summary>
    /// 纯物理闲时服务实现（规范 §3.4）。
    /// 唯一 1s 线程池 Timer 扇出 IdleTick；SystemBusy 缓存轮询 2s，变化才广播。
    /// 首次有 IdleTick 订阅者时懒启动，最后一个取消后自动停。
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

        private const int QUNS_BUSY = 2;
        private const int QUNS_RUNNING_D3D_FULL_SCREEN = 4;
        private const int QUNS_PRESENTATION_MODE = 5;
        private const int QUNS_ACCEPTS_NOTIFICATIONS = 6;
        private const int QUNS_QUIET_TIME = 7;

        private readonly Timer _timer;

        public TimeSpan RawIdle { get; private set; }
        public bool IsSystemBusyCached { get; private set; }

        public event Action<TimeSpan> IdleTick;
        public event Action UserActiveDetected;
        public event Action<bool> SystemBusyChanged;

        public SystemIdleService()
        {
            // 无消费者时惰性启动：TODO 供未来 ScreenLock/TaskScheduler 迁移到纯 IdleService 后订阅启用
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

        private void Tick(object state)
        {
            TimeSpan raw = GetRawIdle();
            bool prevActive = RawIdle <= TimeSpan.Zero;
            RawIdle = raw;
            try { IdleTick?.Invoke(raw); } catch { }

            if (raw <= TimeSpan.FromMilliseconds(1500) && prevActive == false)
            {
                try { UserActiveDetected?.Invoke(); } catch { }
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
                try { SystemBusyChanged?.Invoke(busy); } catch { }
            }
        }

        private static bool QueryBusy()
        {
            try
            {
                if (SHQueryUserNotificationState(out int state) == 0)
                {
                    return state == QUNS_BUSY || state == QUNS_RUNNING_D3D_FULL_SCREEN || state == QUNS_PRESENTATION_MODE;
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