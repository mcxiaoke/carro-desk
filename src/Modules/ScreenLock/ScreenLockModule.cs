using System;
using System.Collections.Generic;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Modules.ScreenLock.Models;
using Microsoft.Win32;
using CarroDesk.Services;
using CarroDesk.Services.Localization;

namespace CarroDesk.Modules.ScreenLock
{
    public class ScreenLockModule : ModuleBase<ScreenLockConfig>, IExitGuard
    {
        public static ScreenLockModule Instance { get; private set; }

        public override string Id => "ScreenLock";
        public override string Name => Loc.T("Tray.ScreenLockTitle", "屏幕锁定与闲时保护");
        public override string Description => "提供闲时伪锁屏保护、PIN验证与键盘输入防御";

        public LockController Controller { get; private set; }

        private bool _sessionLocked;
        private DateTime _pauseUntil = DateTime.MinValue;

        // 闲时业务状态机（规范 §3.4）：纯 IIdleService 广播 + 模块内复刻 IdleDetector 语义
        private const double IdleIntervalMs = 1000;
        private static readonly TimeSpan WarnBefore = TimeSpan.FromSeconds(30);
        private readonly object _idleLock = new object();
        private IIdleService _idleService;
        private bool _idleFired;
        private bool _warnedIdle;
        private double _effectiveMs;
        private double _lastRawMs = -1;

        public bool IsSessionLocked => _sessionLocked;
        public DateTime PauseUntil => _pauseUntil;
        public bool IsPaused => DateTime.Now < _pauseUntil;

        public Action<string> BalloonNotifier { get; set; }

        public ScreenLockModule(ConfigService configService)
        {
            Instance = this;
            Controller = new LockController(configService);
        }

        protected override void OnStart()
        {
            var configMgr = Context.GetService<IConfigManager>();
            var config = Config ?? new ScreenLockConfig();

            _idleService = Context.GetService<IIdleService>();
            if (_idleService != null)
            {
                _idleService.IdleTick += OnIdleTick;
                _idleService.UserActiveDetected += OnUserActive;
            }

            Controller.Unlocked += OnControllerUnlocked;
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        protected override void OnStop()
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            if (_idleService != null)
            {
                _idleService.IdleTick -= OnIdleTick;
                _idleService.UserActiveDetected -= OnUserActive;
                _idleService = null;
            }
            if (Controller != null)
            {
                Controller.Unlocked -= OnControllerUnlocked;
            }
        }

        public override void OnConfigReloaded()
        {
            base.OnConfigReloaded();
            ResetIdleMachine();
            if (Controller != null)
            {
                Controller.ApplyPinFromConfig();
            }
        }

        private void OnIdleTick(TimeSpan rawIdle)
        {
            double thresholdMs = (Config != null ? Config.IdleMinutes : 0) * 60000.0;
            if (thresholdMs <= 0) return;

            bool fireThreshold = false;
            bool fireWarn = false;
            lock (_idleLock)
            {
                if (_idleFired) return;
                double raw = rawIdle.TotalMilliseconds;
                bool hasInput = raw < _lastRawMs || raw < IdleIntervalMs * 2;
                _lastRawMs = raw;
                bool suspended = ShouldSuspendIdle();
                if (hasInput) _effectiveMs = raw;
                else if (!suspended) _effectiveMs += IdleIntervalMs;
                if (suspended) return;

                if (_effectiveMs >= thresholdMs)
                {
                    _idleFired = true;
                    fireThreshold = true;
                }
                else if (WarnBefore.TotalMilliseconds > 0 && thresholdMs > WarnBefore.TotalMilliseconds
                         && _effectiveMs >= thresholdMs - WarnBefore.TotalMilliseconds && !_warnedIdle)
                {
                    _warnedIdle = true;
                    fireWarn = true;
                }
                else if (_effectiveMs < thresholdMs - WarnBefore.TotalMilliseconds)
                {
                    _warnedIdle = false;
                }
            }
            if (fireThreshold)
            {
                var d = Context?.Dispatcher;
                if (d != null) d.BeginInvoke(new Action(OnIdleThresholdReached));
            }
            else if (fireWarn)
            {
                var d = Context?.Dispatcher;
                if (d != null) d.BeginInvoke(new Action(OnIdleWarning));
            }
        }

        private void OnUserActive()
        {
            ResetIdleMachine();
        }

        private void ResetIdleMachine()
        {
            lock (_idleLock)
            {
                _idleFired = false;
                _warnedIdle = false;
                _effectiveMs = 0;
                _lastRawMs = -1;
            }
        }

        private bool ShouldSuspendIdle()
        {
            if (_sessionLocked) return true;
            if (DateTime.Now < _pauseUntil) return true;
            if (_idleService != null && _idleService.IsSystemBusyCached) return true;
            try
            {
                var excl = Config != null ? Config.ExcludeProcesses : null;
                if (excl != null && excl.Count > 0 && ProcessExclusionService.IsExcludedRunning(excl))
                    return true;
            }
            catch { }
            return false;
        }

        private void OnIdleThresholdReached()
        {
            if (_sessionLocked) return;
            Controller.LockSafe();
        }

        private void OnIdleWarning()
        {
            BalloonNotifier?.Invoke(Loc.T("Tray.BalloonIdleWarn", Config?.IdleMinutes ?? 5));
        }

        private void OnControllerUnlocked()
        {
            ResetIdleMachine();
        }

        public void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (Controller == null) return;
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                _sessionLocked = true;
                lock (_idleLock) _idleFired = true; // 会话锁定视为已消费一次触发，解锁后重置
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                _sessionLocked = false;
                if (Config != null && Config.UnlockOnResume && Controller.IsLocked)
                {
                    Controller.Unlock();
                }
                ResetIdleMachine();
            }
        }

        public void LockSafe()
        {
            Controller?.LockSafe();
        }

        public void Unlock()
        {
            Controller?.Unlock();
        }

        public bool RequestBlockExit()
        {
            // 已设置 PIN 时阻止退出并让 Host 弹挑战；未设 PIN 则放行
            return Config != null && !string.IsNullOrEmpty(Config.PinHash) && !string.IsNullOrEmpty(Config.PinSalt);
        }

        public void SetIdleMinutes(int mins)
        {
            if (Config != null)
            {
                Config.IdleMinutes = mins;
                var configMgr = Context?.GetService<IConfigManager>();
                configMgr?.SaveModuleConfig(Id, Config);
            }
            ResetIdleMachine();
        }

        public void PauseFor(TimeSpan span)
        {
            _pauseUntil = DateTime.Now.Add(span);
        }

        public void ResumeIdle()
        {
            _pauseUntil = DateTime.MinValue;
            ResetIdleMachine();
        }

        public override IEnumerable<TrayMenuItem> GetTrayMenuItems()
        {
            // 模块自包含的托盘菜单项暴露
            var items = new List<TrayMenuItem>();

            var lockItem = new TrayMenuItem
            {
                Id = "screenlock_lock_now",
                Header = Loc.T("Tray.LockNow", "立即锁屏"),
                InputGestureText = "Win+L (仿真)",
                ClickAction = () => LockSafe()
            };
            items.Add(lockItem);

            return items;
        }
    }
}