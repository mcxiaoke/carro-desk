using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Modules.Awake.Models;
using CarroDesk.Modules.Awake.Services;
using CarroDesk.Services.Localization;
using CarroDesk.Views;

namespace CarroDesk.Modules.Awake
{
    public class AwakeModule : ModuleBase<AwakeConfig>
    {
        public override string Id => "Awake";
        public override string Name => Loc.T("Tray.AwakeTitle", "保持唤醒 (Awake)");
        public override string Description => Loc.T("Tray.AwakeDesc", "阻止计算机休眠或关闭屏幕");
        public override int Order => 15;
        public override bool DefaultEnabled => true;

        public AwakeService Service { get; private set; }

        private IHotkeyService _hotkeys;
        private TrayMenuItem _trayRoot;
        private int _hotkeyId;
        private DateTime _lastHeaderUpdateTime = DateTime.MinValue;

        public AwakeModule()
        {
        }

        public override void Initialize(IModuleContext context)
        {
            base.Initialize(context);

            var logger = context.GetService<ILoggerService>();
            Service = new AwakeService(context.Dispatcher, logger);
            Service.Initialize(Config);

            Service.StateChanged += OnServiceStateChanged;
            Service.Expired += OnServiceExpired;
            Service.BatteryStateChanged += OnBatteryStateChanged;
            Service.ProcessTriggered += OnProcessTriggered;
            Service.Tick += OnServiceTick;
        }

        protected override void OnStart()
        {
            _hotkeys = Context.GetService<IHotkeyService>();
            Service.Start();
            RegisterHotkey();
            UpdateTrayHeaderAndToolTip();
        }

        protected override void OnStop()
        {
            UnregisterHotkey();
            _hotkeys?.UnregisterAll(Id);
            Service.Stop();
        }

        public override void OnConfigReloaded()
        {
            base.OnConfigReloaded();
            Service.UpdateConfig(Config);
            UnregisterHotkey();
            RegisterHotkey();
            UpdateTrayHeaderAndToolTip();
            RequestRefreshTray();
        }

        public override void OnLanguageChanged()
        {
            base.OnLanguageChanged();
            UpdateTrayHeaderAndToolTip();
            RequestRefreshTray();
        }

        public void SaveConfig()
        {
            try
            {
                if (Config != null && Service != null)
                {
                    Config.Mode = Service.Mode;
                    Config.KeepDisplayOn = Service.KeepDisplayOn;
                }
                var configMgr = Context?.GetService<IConfigManager>();
                configMgr?.SaveModuleConfig(Id, Config);
            }
            catch { }
        }

        public void RequestRefreshTray()
        {
            try { Context?.RequestTrayRefresh(); } catch { }
        }

        private void RegisterHotkey()
        {
            if (Config == null || string.IsNullOrWhiteSpace(Config.Hotkey) || _hotkeys == null)
                return;

            try
            {
                _hotkeyId = _hotkeys.Register(Id, Config.Hotkey, () =>
                {
                    ToggleAwakeQuick();
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

        private void ToggleAwakeQuick()
        {
            if (Service == null) return;

            if (Service.Mode == AwakeMode.Passive)
            {
                int defaultMins = Config != null ? Config.DefaultDurationMinutes : 30;
                if (defaultMins > 0)
                {
                    Service.SetTimed(defaultMins);
                    ShowNotify(Loc.T("Tray.AwakeNotifyTimed", $"已开启保持唤醒 ({defaultMins} 分钟)"));
                }
                else
                {
                    Service.SetIndefinite();
                    ShowNotify(Loc.T("Tray.AwakeNotifyIndefinite", "已开启无限期保持唤醒"));
                }
            }
            else
            {
                Service.SetPassiveByUser();
                ShowNotify(Loc.T("Tray.AwakeNotifyPassive", "已关闭保持唤醒，恢复系统默认电源策略"));
            }
            SaveConfig();
            UpdateTrayHeaderAndToolTip();
            RequestRefreshTray();
        }

        private void OnServiceStateChanged()
        {
            UpdateTrayHeaderAndToolTip();
            RequestRefreshTray();
        }

        private void OnServiceExpired()
        {
            ShowNotify(Loc.T("Tray.AwakeNotifyExpired", "保持唤醒时间已结束，已恢复系统常规电源策略。"));
            SaveConfig();
            UpdateTrayHeaderAndToolTip();
            RequestRefreshTray();
        }

        private void OnBatteryStateChanged(bool isPaused, byte percent)
        {
            if (isPaused)
            {
                ShowNotify(Loc.T("Tray.AwakeNotifyBatteryPaused", "检测到使用电池供电/电量不足，已临时挂起保持唤醒以保护电池。"));
            }
            else
            {
                ShowNotify(Loc.T("Tray.AwakeNotifyBatteryResumed", "已恢复交流电源供电，继续保持唤醒。"));
            }
            UpdateTrayHeaderAndToolTip();
            RequestRefreshTray();
        }

        private void OnProcessTriggered(bool isTriggered, string procName)
        {
            if (isTriggered)
            {
                // 必须走带格式化的重载：默认值里带 {0}，两参重载不会执行 string.Format，
                // 会导致中文丢失进程名、英文显示字面量 {0}
                ShowNotify(Loc.T("Tray.AwakeNotifyProcessActive", "检测到目标进程 '{0}' 正在运行，已自动开启保持唤醒。", procName));
            }
            else
            {
                ShowNotify(Loc.T("Tray.AwakeNotifyProcessEnded", "目标进程已退出，保持唤醒已自动恢复关闭。"));
            }
            UpdateTrayHeaderAndToolTip();
            RequestRefreshTray();
        }

        private void OnServiceTick()
        {
            // 为避免频繁触发菜单重绘，每 10 秒或分钟数跨界时更新一次 Header 倒计时
            if (Service != null && (Service.Mode == AwakeMode.Timed || Service.Mode == AwakeMode.UntilTime))
            {
                var now = DateTime.Now;
                if ((now - _lastHeaderUpdateTime).TotalSeconds >= 10)
                {
                    _lastHeaderUpdateTime = now;
                    UpdateTrayHeaderAndToolTip();
                }
            }
        }

        public string BuildAwakeHeader()
        {
            string baseTitle = Loc.T("Tray.AwakeRoot", "保持唤醒");
            if (Service == null) return baseTitle;

            string status;
            if (Service.IsBatteryPaused)
            {
                status = Loc.T("Tray.AwakeStatusBatteryPaused", "电池暂停");
            }
            else if (Service.IsProcessTriggered)
            {
                status = Loc.T("Tray.AwakeStatusProcess", "进程联动");
            }
            else
            {
                switch (Service.Mode)
                {
                    case AwakeMode.Passive:
                        status = Loc.T("Tray.Disabled", "已禁用");
                        break;
                    case AwakeMode.Indefinite:
                        status = Loc.T("Tray.AwakeStatusIndefinite", "永久");
                        break;
                    case AwakeMode.Timed:
                    case AwakeMode.UntilTime:
                        var rem = Service.RemainingTime;
                        if (rem.TotalHours >= 1)
                        {
                            status = Loc.T("Awake.RemainingHours", "剩 {0}h{1:D2}m", (int)rem.TotalHours, rem.Minutes);
                        }
                        else
                        {
                            status = Loc.T("Awake.RemainingMinutes", "剩 {0}m", Math.Max(1, (int)Math.Ceiling(rem.TotalMinutes)));
                        }
                        break;
                    default:
                        status = Loc.T("Tray.Disabled", "已禁用");
                        break;
                }
            }
            return $"{baseTitle} ({status})";
        }

        public string BuildAwakeToolTip()
        {
            if (Service == null) return Loc.T("Awake.TrayTitle", "CarroDesk - 保持唤醒");

            string modeDesc;
            if (Service.IsBatteryPaused)
            {
                modeDesc = Loc.T("Awake.StatusBatterySuspended", "已因电池供电挂起保持唤醒");
            }
            else if (Service.IsProcessTriggered)
            {
                modeDesc = Loc.T("Awake.StatusProcessLinked", "进程 '{0}' 联动保持唤醒中", Service.ActiveProcessTrigger);
            }
            else
            {
                switch (Service.Mode)
                {
                    case AwakeMode.Passive:
                        modeDesc = Loc.T("Awake.StatusFollowingSystem", "遵循系统默认电源策略（已关闭）");
                        break;
                    case AwakeMode.Indefinite:
                        modeDesc = Loc.T("Awake.StatusIndefinite", "无限期保持唤醒");
                        break;
                    case AwakeMode.Timed:
                    case AwakeMode.UntilTime:
                        var rem = Service.RemainingTime;
                        modeDesc = Loc.T("Awake.StatusTimed", "定时保持唤醒 (剩余 {0} 分钟，到期时间 {1})", (int)rem.TotalMinutes, Service.ExpireTime.ToString("HH:mm"));
                        break;
                    default:
                        modeDesc = Loc.T("Awake.StatusClosed", "已关闭");
                        break;
                }
            }

            string displayDesc = Service.KeepDisplayOn ? Loc.T("Awake.DisplayOn", "保持屏幕常亮") : Loc.T("Awake.DisplayOff", "允许屏幕熄灭");
            return Loc.T("Awake.TooltipFormat", "CarroDesk - 保持唤醒: {0} [{1}]", modeDesc, displayDesc);
        }

        private void UpdateTrayHeaderAndToolTip()
        {
            SetTrayItemSelf(BuildAwakeHeader(), BuildAwakeToolTip());
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

        public override IEnumerable<TrayMenuItem> GetTrayMenuItems()
        {
            var items = new List<TrayMenuItem>();
            var root = new TrayMenuItem
            {
                Id = "awake_root",
                Header = BuildAwakeHeader(),
                ToolTip = BuildAwakeToolTip()
            };
            _trayRoot = root;

            var currentMode = Service != null ? Service.Mode : AwakeMode.Passive;

            // 1. 关闭（遵循系统默认电源策略）
            root.Children.Add(new TrayMenuItem
            {
                Id = "awake_mode_passive",
                Header = Loc.T("Tray.AwakeModePassive", "关闭 (遵循系统电源计划)"),
                IsChecked = currentMode == AwakeMode.Passive,
                ClickAction = () =>
                {
                    Service?.SetPassiveByUser();
                    SaveConfig();
                    UpdateTrayHeaderAndToolTip();
                    RequestRefreshTray();
                }
            });

            // 2. 无限期保持唤醒
            root.Children.Add(new TrayMenuItem
            {
                Id = "awake_mode_indefinite",
                Header = Loc.T("Tray.AwakeModeIndefinite", "无限期保持唤醒"),
                IsChecked = currentMode == AwakeMode.Indefinite,
                ClickAction = () =>
                {
                    Service?.SetIndefinite();
                    SaveConfig();
                    UpdateTrayHeaderAndToolTip();
                    RequestRefreshTray();
                    ShowNotify(Loc.T("Tray.AwakeNotifyIndefinite", "已开启无限期保持唤醒"));
                }
            });

            // 3. 定时保持唤醒 (子菜单)
            var timedSubmenu = new TrayMenuItem
            {
                Id = "awake_mode_timed_root",
                Header = Loc.T("Tray.AwakeModeTimed", "定时保持唤醒"),
                IsChecked = currentMode == AwakeMode.Timed
            };

            int[] presetMinutes = new[] { 15, 30, 60, 120, 240, 480 };
            foreach (var m in presetMinutes)
            {
                string label = m < 60 ? Loc.T("Awake.MinutesLabel", "{0} 分钟", m) : Loc.T("Awake.HoursLabel", "{0} 小时", m / 60);
                timedSubmenu.Children.Add(new TrayMenuItem
                {
                    Id = $"awake_timed_{m}",
                    Header = label,
                    ClickAction = () =>
                    {
                        Service?.SetTimed(m);
                        SaveConfig();
                        UpdateTrayHeaderAndToolTip();
                        RequestRefreshTray();
                        ShowNotify(Loc.T("Awake.TimedOn", "已开启保持唤醒 ({0})", label));
                    }
                });
            }

            timedSubmenu.Children.Add(TrayMenuItem.Separator());
            timedSubmenu.Children.Add(new TrayMenuItem
            {
                Id = "awake_timed_custom",
                Header = Loc.T("Tray.AwakeTimedCustom", "自定义分钟数..."),
                ClickAction = () =>
                {
                    if (PromptMinutes(out int customMins))
                    {
                        Service?.SetTimed(customMins);
                        SaveConfig();
                        UpdateTrayHeaderAndToolTip();
                        RequestRefreshTray();
                        ShowNotify(Loc.T("Awake.TimedCustom", "已开启保持唤醒 ({0} 分钟)", customMins));
                    }
                }
            });
            root.Children.Add(timedSubmenu);

            // 4. 保持至指定时刻 (子菜单)
            var untilSubmenu = new TrayMenuItem
            {
                Id = "awake_mode_until_root",
                Header = Loc.T("Tray.AwakeModeUntil", "保持唤醒至指定时刻"),
                IsChecked = currentMode == AwakeMode.UntilTime
            };

            int[] targetHours = new[] { 18, 20, 22 };
            foreach (var h in targetHours)
            {
                string label = Loc.T("Awake.UntilHour", "至 {0:D2}:00", h);
                if (h == 18) label += Loc.T("Awake.UntilHourOffWork", " (下班)");
                untilSubmenu.Children.Add(new TrayMenuItem
                {
                    Id = $"awake_until_{h}",
                    Header = label,
                    ClickAction = () =>
                    {
                        DateTime target = DateTime.Today.AddHours(h);
                        Service?.SetUntilTime(target);
                        SaveConfig();
                        UpdateTrayHeaderAndToolTip();
                        RequestRefreshTray();
                        ShowNotify(Loc.T("Awake.UntilTime", "已设置保持唤醒至 {0:D2}:00", h));
                    }
                });
            }
            root.Children.Add(untilSubmenu);

            root.Children.Add(TrayMenuItem.Separator());

            // 5. 保持屏幕常亮 (复选开关)
            bool keepDisplay = Service != null ? Service.KeepDisplayOn : true;
            root.Children.Add(new TrayMenuItem
            {
                Id = "awake_keep_display",
                Header = Loc.T("Tray.AwakeKeepDisplayOn", "保持屏幕常亮"),
                IsChecked = keepDisplay,
                ToolTip = Loc.T("Tray.AwakeKeepDisplayTip", "勾选: 系统+屏幕常亮；未勾选: 仅系统不睡眠，允许显示器按时熄灭"),
                ClickAction = () =>
                {
                    Service?.ToggleKeepDisplayOn();
                    SaveConfig();
                    UpdateTrayHeaderAndToolTip();
                    RequestRefreshTray();
                }
            });

            root.Children.Add(TrayMenuItem.Separator());

            // 6. 高级设置
            root.Children.Add(new TrayMenuItem
            {
                Id = "awake_settings",
                Header = Loc.T("Tray.AwakeSettings", "保持唤醒设置..."),
                ClickAction = () =>
                {
                    var win = new AwakeSettingsWindow(this)
                    {
                        WindowStartupLocation = WindowStartupLocation.CenterScreen
                    };
                    win.ShowDialog();
                }
            });

            items.Add(root);
            return items;
        }

        private void ShowNotify(string msg)
        {
            try
            {
                Context?.ShowNotification(msg, "CarroDesk");
            }
            catch { }
        }

        private static bool PromptMinutes(out int minutes)
        {
            minutes = 0;
            var dlg = new Window
            {
                Title = Loc.T("Tray.AwakeTimedCustom", "自定义分钟数..."),
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
                Text = Loc.T("Awake.PromptMinutes", "请输入保持唤醒的分钟数 (1-1440)："),
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
