using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Modules.ScreenLock.Models;
using Microsoft.Win32;
using CarroDesk.Services;
using CarroDesk.Services.Localization;
using CarroDesk.Host.Services;
using CarroDesk.Views;

namespace CarroDesk.Modules.ScreenLock
{
    public class ScreenLockModule : ModuleBase<ScreenLockConfig>, IExitGuard
    {
        public override string Id => "ScreenLock";
        public override string Name => Loc.T("Tray.ScreenLockTitle", "屏幕锁定与闲时保护");
        public override string Description => Loc.T("Lock.ModuleDesc", "提供闲时伪锁屏保护、PIN验证与键盘输入防御");

        public LockController Controller { get; private set; }

        private bool _sessionLocked;
        private DateTime _pauseUntil = DateTime.MinValue;
        private IHotkeyService _hotkeys;
        private int _hotkeyId;

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

        public ScreenLockModule()
        {
            Controller = new LockController(
                () => Context?.GetService<IPinService>(),
                () => Config,
                Context?.GetService<ILoggerService>());
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

            _hotkeys = Context.GetService<IHotkeyService>();
            RegisterHotkey();

            Controller.Unlocked += OnControllerUnlocked;
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        public override void Dispose()
        {
            // LockController 订阅了静态 SystemEvents.DisplaySettingsChanged，
            // 并持有全局低级键盘钩子；不释放会同时泄漏事件引用与钩子句柄。
            Controller?.Dispose();
            base.Dispose();
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
            UnregisterHotkey();
            _hotkeys?.UnregisterAll(Id);
            if (Controller != null)
            {
                Controller.Unlocked -= OnControllerUnlocked;
            }
        }

        public override void OnConfigReloaded()
        {
            base.OnConfigReloaded();
            ResetIdleMachine();
            UnregisterHotkey();
            RegisterHotkey();
            if (Controller != null)
            {
                Controller.ApplyPinFromConfig();
            }
            UpdateTrayHeaderAndToolTip();
        }

        private void RegisterHotkey()
        {
            if (Config == null || string.IsNullOrWhiteSpace(Config.Hotkey) || _hotkeys == null)
                return;

            try
            {
                _hotkeyId = _hotkeys.Register(Id, Config.Hotkey, () =>
                {
                    LockSafe();
                }, out _);
            }
            catch { }
        }

        private void UnregisterHotkey()
        {
            if (_hotkeyId > 0 && _hotkeys != null)
            {
                try
                {
                    _hotkeys.Unregister(Id, _hotkeyId);
                    _hotkeyId = 0;
                }
                catch { }
            }
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
            if (!Controller.LockSafe())
            {
                // 锁定失败（已内部回滚，不会有半锁定状态）。重置空闲状态机，
                // 否则 _idleFired 已置位会让后续空闲周期不再重试，锁屏彻底失效。
                ResetIdleMachine();
            }
        }

        private void OnIdleWarning()
        {
            Context?.ShowNotification(Loc.T("Tray.BalloonIdleWarn", Config?.IdleMinutes ?? 5));
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

                // SystemEvents 在自己的专用线程上触发本回调，不能直接操作 WPF 窗口。
                var dispatcher = Context?.Dispatcher;
                if (dispatcher == null)
                {
                    UnlockFromSessionUnlock();
                }
                else if (dispatcher.CheckAccess())
                {
                    UnlockFromSessionUnlock();
                }
                else
                {
                    dispatcher.BeginInvoke(new Action(UnlockFromSessionUnlock));
                }
            }
        }

        private void UnlockFromSessionUnlock()
        {
            if (Config != null && Config.UnlockOnResume && Controller.IsLocked)
            {
                Controller.Unlock();
            }
            ResetIdleMachine();
        }

        public bool LockSafe()
        {
            var controller = Controller;
            if (controller == null) return false;
            if (controller.LockSafe()) return true;

            // 锁定失败（LockController 内部已回滚，不会留下"键盘被钩住但无 PIN 界面"的半锁定态）。
            // 必须重置空闲状态机：否则 _idleFired 已置位会让后续空闲周期不再重试，锁屏彻底失效。
            ResetIdleMachine();
            return false;
        }

        public void Unlock()
        {
            Controller?.Unlock();
        }

        public bool RequestBlockExit()
        {
            var pinService = Context?.GetService<IPinService>();
            return pinService?.IsConfigured ?? false;
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
                InputGestureText = Config?.Hotkey ?? "Ctrl+Alt+L",
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
            pauseRoot.Children.Add(TrayMenuItem.Separator());
            pauseRoot.Children.Add(new TrayMenuItem
            {
                Id = "screenlock_pause_custom",
                Header = Loc.T("Tray.PauseCustom", "自定义暂停分钟数..."),
                ClickAction = () =>
                {
                    if (PromptPauseMinutes(out int mins))
                    {
                        PauseFor(TimeSpan.FromMinutes(mins));
                        ShowNotifySelf(Loc.T("Tray.BalloonPause", PauseUntil));
                    }
                }
            });
            root.Children.Add(pauseRoot);

            // 4. 锁屏与闲时保护设置入口
            root.Children.Add(TrayMenuItem.Separator());
            root.Children.Add(new TrayMenuItem
            {
                Id = "screenlock_settings",
                Header = Loc.T("Tray.ScreenLockSettings", "屏幕保护设置..."),
                ClickAction = () =>
                {
                    try
                    {
                        var pinService = Context?.GetService<IPinService>();
                        var cfgMgr = Context?.GetService<IConfigManager>() as ConfigManager;
                        var win = new ConfigEditorWindow(pinService, cfgMgr, () =>
                        {
                            OnConfigReloaded();
                        })
                        {
                            WindowStartupLocation = WindowStartupLocation.CenterScreen
                        };
                        win.ShowDialog();
                    }
                    catch { }
                }
            });

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

        private static bool PromptPauseMinutes(out int minutes)
        {
            minutes = 0;
            var dlg = new Window
            {
                Title = Loc.T("Tray.PauseCustom", "自定义暂停分钟数..."),
                Width = 360,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = System.Windows.Media.Brushes.White,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI")
            };
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = new TextBlock
            {
                Text = Loc.T("Lock.PromptPauseMinutes", "请输入暂停计时的分钟数 (1-1440)："),
                Margin = new Thickness(0, 0, 0, 8),
                FontSize = 13,
                Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#FF374151")
            };
            var txt = new TextBox
            {
                Text = "45",
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0, 4, 0),
                FontSize = 13
            };
            txt.SelectAll();

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var btnOk = new Button { Content = Loc.T("Common.Ok", "确定"), Width = 70, Height = 28, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var btnCancel = new Button { Content = Loc.T("Common.Cancel", "取消"), Width = 70, Height = 28, IsCancel = true };

            btnOk.Click += (s, e) => { dlg.DialogResult = true; dlg.Close(); };
            btnCancel.Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };

            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);

            Grid.SetRow(lbl, 0);
            Grid.SetRow(txt, 1);
            Grid.SetRow(btnPanel, 2);

            grid.Children.Add(lbl);
            grid.Children.Add(txt);
            grid.Children.Add(btnPanel);

            dlg.Content = grid;
            dlg.Loaded += (s, e) => txt.Focus();

            if (dlg.ShowDialog() == true)
            {
                string input = txt.Text.Trim();
                if (int.TryParse(input, out int m) && m > 0 && m <= 1440)
                {
                    minutes = m;
                    return true;
                }
                MessageBox.Show(Loc.T("Msg.InvalidMinutes", "请输入 1 到 1440 之间的有效分钟数。"), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }
    }
}