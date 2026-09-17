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
            UpdateTrayHeaderAndToolTip();
        }

        public override void OnLanguageChanged()
        {
            base.OnLanguageChanged();
            UpdateTrayHeaderAndToolTip();
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

        private TrayMenuItem _trayRoot;

        public void SetIdleMinutes(int mins)
        {
            if (Config != null)
            {
                Config.IdleMinutes = mins;
                var configMgr = Context?.GetService<IConfigManager>();
                configMgr?.SaveModuleConfig(Id, Config);
            }
            ResetIdleMachine();
            UpdateTrayHeaderAndToolTip();
            RequestTrayRefreshSelf();
        }

        public void PauseFor(TimeSpan span)
        {
            _pauseUntil = DateTime.Now.Add(span);
            UpdateTrayHeaderAndToolTip();
            RequestTrayRefreshSelf();
        }

        public void ResumeIdle()
        {
            _pauseUntil = DateTime.MinValue;
            ResetIdleMachine();
            UpdateTrayHeaderAndToolTip();
            RequestTrayRefreshSelf();
        }

        public string BuildScreenLockHeader()
        {
            string baseTitle = Loc.T("Tray.ScreenLockRoot", "屏幕保护");
            string status;
            if (IsPaused)
            {
                status = Loc.T("Tray.Paused", "已暂停");
            }
            else
            {
                int mins = Config != null ? Config.IdleMinutes : 0;
                if (mins <= 0)
                {
                    status = Loc.T("Tray.Disabled", "已禁用");
                }
                else
                {
                    status = Loc.T("Tray.IdleMinutesFormat", mins);
                }
            }
            return $"{baseTitle} ({status})";
        }

        public string BuildScreenLockToolTip()
        {
            if (IsPaused)
            {
                return Loc.T("Tray.StatusPausedDetail", PauseUntil);
            }
            int mins = Config != null ? Config.IdleMinutes : 0;
            if (mins <= 0)
            {
                return Loc.T("Tray.StatusDisabledDetail", "空闲锁定已禁用");
            }
            return Loc.T("Tray.StatusIdleDetail", mins);
        }

        private void UpdateTrayHeaderAndToolTip()
        {
            SetTrayItemSelf(BuildScreenLockHeader(), BuildScreenLockToolTip());
        }

        private void SetTrayItemSelf(string header, string toolTip)
        {
            if (_trayRoot == null) return;
            var d = Context?.Dispatcher;
            if (d != null && !d.CheckAccess())
            {
                d.BeginInvoke(new Action(() => SetTrayItemSelf(header, toolTip)));
                return;
            }
            _trayRoot.Header = header;
            _trayRoot.ToolTip = toolTip;
        }

        private void RequestTrayRefreshSelf()
        {
            try { Context?.RequestTrayRefresh(); } catch { }
        }

        public override IEnumerable<TrayMenuItem> GetTrayMenuItems()
        {
            // 按模块二级聚合规范：本模块仅输出单一根节点，所有控制项收敛入二级菜单
            var items = new List<TrayMenuItem>();
            int currentMinutes = Config != null ? Config.IdleMinutes : 0;

            var root = new TrayMenuItem
            {
                Id = "screenlock_root",
                Header = BuildScreenLockHeader(),
                ToolTip = BuildScreenLockToolTip()
            };
            _trayRoot = root;

            // 1. 核心动作置顶：立即锁定
            var lockItem = new TrayMenuItem
            {
                Id = "screenlock_lock_now",
                Header = Loc.T("Tray.LockNow", "立即锁定"),
                InputGestureText = "Win+L (仿真)",
                ClickAction = () => LockSafe()
            };
            root.Children.Add(lockItem);

            root.Children.Add(TrayMenuItem.Separator());

            // 2. 空闲锁定档位：当前档位用 IsChecked 表达，"0-禁用" 即禁用态
            var idleRoot = new TrayMenuItem
            {
                Id = "screenlock_idle_root",
                Header = Loc.T("Tray.IdleLock", "空闲锁定")
            };
            AddIdlePreset(idleRoot, 0, Loc.T("Tray.IdleDisabled", "0 - 禁用"), currentMinutes);
            foreach (var m in new[] { 1, 3, 5, 10, 15, 30 })
            {
                AddIdlePreset(idleRoot, m, Loc.T("Tray.IdleMinutesFormat", m), currentMinutes);
            }
            root.Children.Add(idleRoot);

            // 3. 暂停计时：暂停态以其根节点 IsChecked 表达
            var pauseRoot = new TrayMenuItem
            {
                Id = "screenlock_pause_root",
                Header = Loc.T("Tray.PauseTimer", "暂停计时"),
                IsChecked = IsPaused,
                ToolTip = IsPaused ? Loc.T("Tray.StatusPausedDetail", PauseUntil) : null
            };
            pauseRoot.Children.Add(new TrayMenuItem
            {
                Id = "screenlock_pause_30",
                Header = Loc.T("Tray.Pause30Min", "暂停 30 分钟"),
                ClickAction = () => { PauseFor(TimeSpan.FromMinutes(30)); ShowNotifySelf(Loc.T("Tray.BalloonPause", PauseUntil)); }
            });
            pauseRoot.Children.Add(new TrayMenuItem
            {
                Id = "screenlock_pause_1h",
                Header = Loc.T("Tray.Pause1Hour", "暂停 1 小时"),
                ClickAction = () => { PauseFor(TimeSpan.FromHours(1)); ShowNotifySelf(Loc.T("Tray.BalloonPause", PauseUntil)); }
            });
            pauseRoot.Children.Add(new TrayMenuItem
            {
                Id = "screenlock_resume",
                Header = Loc.T("Tray.ResumeTimer", "恢复计时"),
                IsChecked = IsPaused,
                ClickAction = () => ResumeIdle()
            });
            root.Children.Add(pauseRoot);

            items.Add(root);
            return items;
        }

        private void AddIdlePreset(TrayMenuItem root, int mins, string header, int currentMinutes)
        {
            root.Children.Add(new TrayMenuItem
            {
                Id = "screenlock_idle_" + mins,
                Header = header,
                IsChecked = mins == currentMinutes,
                ClickAction = () => SetIdleMinutes(mins)
            });
        }

        private void ShowNotifySelf(string msg)
        {
            try { Context?.ShowNotification(msg, "CarroDesk"); } catch { }
        }
    }
}