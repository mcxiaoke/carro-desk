using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
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
        public static AwakeModule Instance { get; private set; }

        public override string Id => "Awake";
        public override string Name => Loc.T("Tray.AwakeTitle", "保持唤醒 (Awake)");
        public override string Description => Loc.T("Tray.AwakeDesc", "阻止计算机休眠或关闭屏幕");
        public override int Order => 15;
        public override bool DefaultEnabled => true;

        public AwakeService Service { get; private set; }
        public Action<string> NotificationCallback { get; set; }

        private IHotkeyService _hotkeys;
        private TrayMenuItem _trayRoot;
        private int _hotkeyId;
        private DateTime _lastHeaderUpdateTime = DateTime.MinValue;

        public AwakeModule()
        {
            Instance = this;
        }

        public override void RegisterConfig(IConfigRegistry registry)
        {
            registry?.RegisterDefault(Id, AwakeConfig.CreateDefault);
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
                Service.SetPassive();
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
                ShowNotify(Loc.T("Tray.AwakeNotifyProcessActive", $"检测到目标进程 '{procName}' 运行，已自动保持唤醒。"));
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
                            status = $"剩 {(int)rem.TotalHours}h{rem.Minutes:D2}m";
                        }
                        else
                        {
                            status = $"剩 {Math.Max(1, (int)Math.Ceiling(rem.TotalMinutes))}m";
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
            if (Service == null) return "CarroDesk - 保持唤醒";

            string modeDesc;
            if (Service.IsBatteryPaused)
            {
                modeDesc = "已因电池供电挂起保持唤醒";
            }
            else if (Service.IsProcessTriggered)
            {
                modeDesc = $"进程 '{Service.ActiveProcessTrigger}' 联动保持唤醒中";
            }
            else
            {
                switch (Service.Mode)
                {
                    case AwakeMode.Passive:
                        modeDesc = "遵循系统默认电源策略（已关闭）";
                        break;
                    case AwakeMode.Indefinite:
                        modeDesc = "无限期保持唤醒";
                        break;
                    case AwakeMode.Timed:
                    case AwakeMode.UntilTime:
                        var rem = Service.RemainingTime;
                        modeDesc = $"定时保持唤醒 (剩余 {(int)rem.TotalMinutes} 分钟，到期时间 {Service.ExpireTime:HH:mm})";
                        break;
                    default:
                        modeDesc = "已关闭";
                        break;
                }
            }

            string displayDesc = Service.KeepDisplayOn ? "保持屏幕常亮" : "允许屏幕熄灭";
            return $"CarroDesk - 保持唤醒: {modeDesc} [{displayDesc}]";
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
                    Service?.SetPassive();
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
                string label = m < 60 ? $"{m} 分钟" : $"{m / 60} 小时";
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
                        ShowNotify($"已开启保持唤醒 ({label})");
                    }
                });
            }
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
                string label = $"至 {h:D2}:00";
                if (h == 18) label += " (下班)";
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
                        ShowNotify($"已设置保持唤醒至 {h:D2}:00");
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
                NotificationCallback?.Invoke(msg);
                Context?.ShowNotification(msg, "CarroDesk");
            }
            catch { }
        }
    }
}
