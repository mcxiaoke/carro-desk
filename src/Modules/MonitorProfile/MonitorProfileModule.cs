using System;
using System.Collections.Generic;
using System.Linq;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Modules.MonitorProfile.Models;
using CarroDesk.Modules.MonitorProfile.Services;
using CarroDesk.Modules.MonitorProfile.Views;
using CarroDesk.Services.Localization;

namespace CarroDesk.Modules.MonitorProfile
{
    public class MonitorProfileModule : ModuleBase<MonitorProfileConfig>
    {
        public override string Id => "MonitorProfile";
        public override string Name => Loc.T("Tray.MonitorProfileTitle", "显示器亮度与情境");
        public override string Description => Loc.T("Tray.MonitorProfileDesc", "多情境亮度与对比度管理、按时间段自动调节与全局快捷键支持");
        public override int Order => 30;
        public override bool DefaultEnabled => true;

        public MonitorDdcService DdcService { get; private set; }
        public ProfileScheduleEngine ScheduleEngine { get; private set; }

        public MonitorProfileModule()
        {
        }

        public override void Initialize(IModuleContext context)
        {
            base.Initialize(context);

            var logger = context.GetService<ILoggerService>();
            DdcService = new MonitorDdcService(logger);
            ScheduleEngine = new ProfileScheduleEngine(DdcService, context.Dispatcher, logger);

            ScheduleEngine.StateChanged += OnEngineStateChanged;
            ScheduleEngine.SettingApplied += OnSettingApplied;
        }

        protected override void OnStart()
        {
            ScheduleEngine.Start(Config);
            RegisterManagedHotkeys();
            UpdateTrayHeaderAndTooltip();
        }

        protected override void OnStop()
        {
            ScheduleEngine.Stop();
        }

        public override void OnConfigReloaded()
        {
            base.OnConfigReloaded();
            ScheduleEngine.UpdateConfig(Config);
            UpdateTrayHeaderAndTooltip();
        }

        public override void OnLanguageChanged()
        {
            base.OnLanguageChanged();
            UpdateTrayHeaderAndTooltip();
        }

        private void OnEngineStateChanged()
        {
            UpdateTrayHeaderAndTooltip();
            RequestTrayRefresh();
        }

        private void OnSettingApplied(string profile, int brightness, int contrast)
        {
            UpdateTrayHeaderAndTooltip();
        }

        public bool SaveAndApplyConfig(MonitorProfileConfig newConfig)
        {
            var previous = Config?.Clone();
            Config = newConfig ?? new MonitorProfileConfig();
            if (!SaveConfig())
            {
                if (previous != null) Config = previous;
                return false;
            }
            OnConfigReloaded();
            return true;
        }

        #region Global Hotkeys

        private void RegisterManagedHotkeys()
        {
            RegisterManagedHotkey(() => Config?.Hotkeys?.SwitchToDailyMode, () =>
            {
                ScheduleEngine?.SwitchProfile("Daily");
                SaveConfig();
                LogInfo("已切换至显示器日常模式 (Daily)");
            });

            RegisterManagedHotkey(() => Config?.Hotkeys?.SwitchToGameMode, () =>
            {
                ScheduleEngine?.SwitchProfile("Game");
                SaveConfig();
                LogInfo("已切换至显示器游戏模式 (Game)");
            });

            RegisterManagedHotkey(() => Config?.Hotkeys?.SwitchToNightMode, () =>
            {
                ScheduleEngine?.SwitchProfile("Night");
                SaveConfig();
                LogInfo("已切换至显示器夜间模式 (Night)");
            });

            RegisterManagedHotkey(() => Config?.Hotkeys?.ManualRefresh, () =>
            {
                ScheduleEngine?.ApplyCurrentSetting(force: true);
                LogInfo("已刷新并重新应用显示器设置");
            });

            RegisterManagedHotkey(() => Config?.Hotkeys?.IncreaseBrightness, () =>
            {
                int step = Config?.BrightnessStep > 0 ? Config.BrightnessStep : 5;
                ScheduleEngine?.StepBrightness(step);
            });

            RegisterManagedHotkey(() => Config?.Hotkeys?.DecreaseBrightness, () =>
            {
                int step = Config?.BrightnessStep > 0 ? Config.BrightnessStep : 5;
                ScheduleEngine?.StepBrightness(-step);
            });
        }

        #endregion

        #region Tray Menu Items

        public string BuildTrayHeader()
        {
            string title = Loc.T("Tray.MonitorProfileRoot", "显示器亮度");
            if (Config == null || !Config.Enabled)
            {
                return $"{title} ({Loc.T("Tray.Disabled", "已禁用")})";
            }

            string active = Config.ActiveProfile ?? "Daily";
            int curB = ScheduleEngine != null && ScheduleEngine.CurrentBrightness >= 0 ? ScheduleEngine.CurrentBrightness : -1;

            if (curB >= 0)
            {
                return $"{title} ({active} · {curB}%)";
            }
            return $"{title} ({active})";
        }

        private void UpdateTrayHeaderAndTooltip()
        {
            int curB = ScheduleEngine != null ? ScheduleEngine.CurrentBrightness : -1;
            int curC = ScheduleEngine != null ? ScheduleEngine.CurrentContrast : -1;
            string tip = curB >= 0
                ? Loc.T("Tray.MonitorProfileTipFormat", "当前显示器配置: {0} (亮度: {1}%, 对比度: {2}%)", Config?.ActiveProfile, curB, curC)
                : null;
            SetTrayItemSelf(BuildTrayHeader(), tip);
        }

        public override IEnumerable<TrayMenuItem> GetTrayMenuItems()
        {
            var items = new List<TrayMenuItem>();
            var cfg = Config ?? MonitorProfileConfig.CreateDefault();

            var root = new TrayMenuItem
            {
                Id = "monitorprofile_root",
                Header = BuildTrayHeader(),
                ToolTip = Loc.T("Tray.MonitorProfileTooltip", "显示器亮度与情境模式")
            };
            TrayRoot = root;

            // 1. 情境模式单选列表
            if (cfg.Profiles != null)
            {
                foreach (var kvp in cfg.Profiles)
                {
                    string profileKey = kvp.Key;
                    var setting = ScheduleEngine?.GetActiveSettingForProfile(profileKey);
                    string detail = setting != null ? $" ({setting.Brightness}% / {setting.Contrast}%)" : "";

                    root.Children.Add(new TrayMenuItem
                    {
                        Id = $"monitor_mode_{profileKey}",
                        Header = $"{profileKey}{detail}",
                        IsChecked = string.Equals(cfg.ActiveProfile, profileKey, StringComparison.OrdinalIgnoreCase),
                        ClickAction = () =>
                        {
                            ScheduleEngine?.SwitchProfile(profileKey);
                            SaveConfig();
                            RequestTrayRefresh();
                        }
                    });
                }
            }

            root.Children.Add(TrayMenuItem.Separator());

            // 2. 常用亮度快捷档位
            int step = cfg.BrightnessStep > 0 ? cfg.BrightnessStep : 5;
            root.Children.Add(new TrayMenuItem
            {
                Id = "monitor_bright_up",
                Header = Loc.T("Tray.MonitorProfileIncrease", "增加亮度 (+{0}%)", step),
                InputGestureText = cfg.Hotkeys?.IncreaseBrightness ?? "Ctrl+Shift+Up",
                ClickAction = () => ScheduleEngine?.StepBrightness(step)
            });

            root.Children.Add(new TrayMenuItem
            {
                Id = "monitor_bright_down",
                Header = Loc.T("Tray.MonitorProfileDecrease", "降低亮度 (-{0}%)", step),
                InputGestureText = cfg.Hotkeys?.DecreaseBrightness ?? "Ctrl+Shift+Down",
                ClickAction = () => ScheduleEngine?.StepBrightness(-step)
            });

            // 预设快捷档位
            var presetsNode = new TrayMenuItem
            {
                Id = "monitor_bright_presets",
                Header = Loc.T("Tray.MonitorProfilePresets", "常用亮度预设")
            };
            int[] presetValues = new int[] { 100, 75, 50, 25 };
            foreach (var pv in presetValues)
            {
                int val = pv;
                presetsNode.Children.Add(new TrayMenuItem
                {
                    Id = $"monitor_preset_{val}",
                    Header = $"{val}%",
                    ClickAction = () => ScheduleEngine?.SetDirectValues(val, 70)
                });
            }
            root.Children.Add(presetsNode);

            root.Children.Add(TrayMenuItem.Separator());

            // 3. 自动时间段计划开关
            root.Children.Add(new TrayMenuItem
            {
                Id = "monitor_auto_schedule",
                Header = Loc.T("Tray.MonitorProfileAutoSchedule", "启用时间段自动调节"),
                IsChecked = cfg.AutoSchedule,
                ClickAction = () =>
                {
                    cfg.AutoSchedule = !cfg.AutoSchedule;
                    SaveConfig();
                    ScheduleEngine?.ApplyCurrentSetting(force: true);
                    RequestTrayRefresh();
                }
            });

            // 4. 立即重新应用
            root.Children.Add(new TrayMenuItem
            {
                Id = "monitor_refresh_now",
                Header = Loc.T("Tray.MonitorProfileRefreshNow", "重新探测并应用当前设置"),
                InputGestureText = cfg.Hotkeys?.ManualRefresh ?? "Ctrl+Shift+R",
                ClickAction = () =>
                {
                    ScheduleEngine?.ApplyCurrentSetting(force: true);
                    LogInfo("已重新探测并应用显示器设置");
                }
            });

            root.Children.Add(TrayMenuItem.Separator());

            // 5. 设置编辑入口
            root.Children.Add(new TrayMenuItem
            {
                Id = "monitor_settings_window",
                Header = Loc.T("Tray.MonitorProfileSettings", "显示器配置与计划..."),
                ClickAction = () =>
                {
                    try
                    {
                        var win = new MonitorProfileSettingsWindow(this)
                        {
                            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen
                        };
                        win.ShowDialog();
                        RequestTrayRefresh();
                    }
                    catch { }
                }
            });

            items.Add(root);
            return items;
        }

        #endregion

        public override void Dispose()
        {
            ScheduleEngine?.Dispose();
            base.Dispose();
        }
    }
}
