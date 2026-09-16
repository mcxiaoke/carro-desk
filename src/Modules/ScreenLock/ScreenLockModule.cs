using System;
using System.Collections.Generic;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Host.Services;
using CarroDesk.Modules.ScreenLock.Models;
using Microsoft.Win32;
using ScreenLock.Services;
using ScreenLock.Services.Localization;

namespace CarroDesk.Modules.ScreenLock
{
    public class ScreenLockModule : ModuleBase<ScreenLockConfig>, IExitGuard
    {
        public static ScreenLockModule Instance { get; private set; }

        public override string Id => "ScreenLock";
        public override string Name => Loc.T("Tray.ScreenLockTitle", "屏幕锁定与闲时保护");
        public override string Description => "提供闲时伪锁屏保护、PIN验证与键盘输入防御";

        public LockController Controller { get; private set; }
        public IdleDetector Idle { get; private set; }

        private bool _sessionLocked;
        private DateTime _pauseUntil = DateTime.MinValue;

        public bool IsSessionLocked => _sessionLocked;
        public DateTime PauseUntil => _pauseUntil;
        public bool IsPaused => DateTime.Now < _pauseUntil;

        public Action<string> BalloonNotifier { get; set; }

        public ScreenLockModule(ConfigService configService)
        {
            Instance = this;
            Controller = new LockController(configService);
            Idle = new IdleDetector();
        }

        protected override void OnStart()
        {
            var configMgr = Context.GetService<IConfigManager>();
            var config = Config ?? new ScreenLockConfig();

            Idle.Threshold = TimeSpan.FromMinutes(config.IdleMinutes);
            Idle.WarnBefore = TimeSpan.FromSeconds(30);
            Idle.ShouldSuspend = ShouldSuspendIdle;
            Idle.Warning += OnIdleWarning;
            Idle.ThresholdReached += OnIdleThresholdReached;
            Idle.Start();

            Controller.Unlocked += OnControllerUnlocked;
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        protected override void OnStop()
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            if (Idle != null)
            {
                Idle.Warning -= OnIdleWarning;
                Idle.ThresholdReached -= OnIdleThresholdReached;
                Idle.Stop();
            }
            if (Controller != null)
            {
                Controller.Unlocked -= OnControllerUnlocked;
            }
        }

        public override void OnConfigReloaded()
        {
            base.OnConfigReloaded();
            if (Idle != null && Config != null)
            {
                Idle.Threshold = TimeSpan.FromMinutes(Config.IdleMinutes);
                Idle.Reset();
            }
            if (Controller != null)
            {
                Controller.ApplyPinFromConfig();
            }
        }

        private bool ShouldSuspendIdle()
        {
            if (_sessionLocked) return true;
            if (DateTime.Now < _pauseUntil) return true;
            if (IdleDetector.IsSystemBusy()) return true;
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
            Idle?.Reset();
        }

        public void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (Idle == null || Controller == null) return;
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                _sessionLocked = true;
                Idle.Suspend();
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                _sessionLocked = false;
                if (Config != null && Config.UnlockOnResume && Controller.IsLocked)
                {
                    Controller.Unlock();
                }
                Idle.Reset();
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
            if (Idle != null)
            {
                Idle.Threshold = TimeSpan.FromMinutes(mins);
                Idle.Reset();
            }
        }

        public void PauseFor(TimeSpan span)
        {
            _pauseUntil = DateTime.Now.Add(span);
        }

        public void ResumeIdle()
        {
            _pauseUntil = DateTime.MinValue;
            Idle?.Reset();
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
