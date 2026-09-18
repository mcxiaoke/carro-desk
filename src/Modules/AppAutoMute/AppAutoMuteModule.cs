using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Modules.AppAutoMute.Models;
using CarroDesk.Services;
using CarroDesk.Services.Localization;

namespace CarroDesk.Modules.AppAutoMute
{
    public class AppAutoMuteModule : ModuleBase<AppAutoMuteConfig>
    {
        public override string Id => "AppAutoMute";
        public override string Name => "应用前后台智能静音";
        public override string Description => "当目标应用位于后台时自动静音，切回前台时自动恢复发声";

        private IAudioService _audioService;
        private IForegroundTracker _foregroundTracker;
        private IHotkeyService _hotkeys;

        private readonly DispatcherTimer _muteTimer;
        private readonly DispatcherTimer _unmuteTimer;

        private string _lastTargetProc;
        private TrayMenuItem _trayRoot;
        private TrayMenuItem _toggleItem;

        public bool IsEnabledUser => Config != null && Config.Enabled;

        public AppAutoMuteModule()
        {

            _muteTimer = new DispatcherTimer();
            _muteTimer.Tick += OnMuteTimerTick;

            _unmuteTimer = new DispatcherTimer();
            _unmuteTimer.Tick += OnUnmuteTimerTick;
        }

        protected override void OnStart()
        {
            _audioService = Context.GetService<IAudioService>();
            _foregroundTracker = Context.GetService<IForegroundTracker>();
            _hotkeys = Context.GetService<IHotkeyService>();

            RegisterHotkey();
            _foregroundTracker.ForegroundChanged += OnForegroundChanged;
            _foregroundTracker.UpdateCurrent();
            EvaluateForeground(_foregroundTracker.CurrentProcessName);
        }

        protected override void OnStop()
        {
            _foregroundTracker.ForegroundChanged -= OnForegroundChanged;
            _muteTimer.Stop();
            _unmuteTimer.Stop();
            UnregisterHotkey();
            _hotkeys?.UnregisterAll(Id);

            // 核心安全保护：停用时强制全量解除目标应用静音
            UnmuteAllTargets();
        }

        public override void OnConfigReloaded()
        {
            base.OnConfigReloaded();
            UnregisterHotkey();
            RegisterHotkey();
            SetCheckedSelf(IsEnabledUser);
        }

        public override void OnLanguageChanged()
        {
            base.OnLanguageChanged();
            UpdateTrayHeader();
            if (_toggleItem != null)
            {
                _toggleItem.Header = Loc.T("Tray.AutoMuteToggle", "启用后台自动静音");
            }
        }

        private int _hotkeyId;

        private void RegisterHotkey()
        {
            if (Config == null || string.IsNullOrEmpty(Config.Hotkey))
                return;

            try
            {
                _hotkeyId = _hotkeys.Register(Id, Config.Hotkey, () =>
                {
                    ToggleEnabled();
                }, out _);
            }
            catch { }
        }

        private void UnregisterHotkey()
        {
            if (_hotkeyId > 0)
            {
                try
                {
                    _hotkeys?.Unregister(Id, _hotkeyId);
                    _hotkeyId = 0;
                }
                catch { }
            }
        }

        public void ToggleEnabled()
        {
            if (Config == null) return;
            Config.Enabled = !Config.Enabled;

            SetCheckedSelf(Config.Enabled);

            var configMgr = Context?.GetService<IConfigManager>();
            configMgr?.SaveModuleConfig(Id, Config);

            string msg = Config.Enabled ? "应用自动静音已启用" : "应用自动静音已禁用 (已恢复所有声音)";
            Context?.ShowNotification(msg);

            if (!Config.Enabled)
            {
                _muteTimer.Stop();
                _unmuteTimer.Stop();
                UnmuteAllTargets();
            }
            else
            {
                EvaluateForeground(_foregroundTracker.CurrentProcessName);
            }
        }

        public string BuildAutoMuteHeader()
        {
            string baseTitle = Loc.T("Tray.AutoMuteRoot", "应用后台静音");
            string status = IsEnabledUser ? Loc.T("Tray.Enabled", "已启用") : Loc.T("Tray.Disabled", "已禁用");
            return $"{baseTitle} ({status})";
        }

        private void UpdateTrayHeader()
        {
            if (_trayRoot == null) return;
            var d = Context?.Dispatcher;
            if (d != null && !d.CheckAccess())
            {
                d.BeginInvoke(new Action(UpdateTrayHeader));
                return;
            }
            _trayRoot.Header = BuildAutoMuteHeader();
        }

        /// <summary>节点属性变更若在后台线程触发，模块自行 Dispatcher 封送回 UI（规范 §4.3）。</summary>
        private void SetCheckedSelf(bool value)
        {
            var d = Context?.Dispatcher;
            if (d != null && !d.CheckAccess())
            {
                d.BeginInvoke(new Action(() => SetCheckedSelf(value)));
                return;
            }
            if (_toggleItem != null) _toggleItem.IsChecked = value;
            if (_trayRoot != null) _trayRoot.Header = BuildAutoMuteHeader();
        }

        private void OnForegroundChanged(IntPtr hwnd, string procName)
        {
            if (!IsEnabledUser) return;
            EvaluateForeground(procName);
        }

        private void EvaluateForeground(string procName)
        {
            if (string.IsNullOrEmpty(procName) || Config == null) return;

            bool isWhitelist = string.Equals(Config.Mode, "Whitelist", StringComparison.OrdinalIgnoreCase);

            // 黑名单模式下，若 TargetApps 为空则无需静音任何应用
            if (!isWhitelist && (Config.TargetApps == null || Config.TargetApps.Count == 0))
                return;

            if (isWhitelist)
            {
                // 白名单模式：任何进程切入当前前台，都必须解除静音恢复出声
                _lastTargetProc = procName;
                _unmuteTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, Config.UnmuteDelayMs));
                _unmuteTimer.Start();

                // 同时启动静音计时器，延时静音切入后台的非白名单非前台进程
                _muteTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, Config.MuteDelayMs));
                _muteTimer.Start();
            }
            else
            {
                // 黑名单模式
                bool isTarget = ProcessHelper.ContainsProcess(Config.TargetApps, procName);
                if (isTarget)
                {
                    // 前台是目标黑名单应用：停止静音计时，启动恢复声音计时
                    _muteTimer.Stop();
                    _lastTargetProc = procName;
                    _unmuteTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, Config.UnmuteDelayMs));
                    _unmuteTimer.Start();
                }
                else
                {
                    // 前台切出到其他非黑名单应用：停止恢复计时，启动黑名单静音计时
                    _unmuteTimer.Stop();
                    _muteTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, Config.MuteDelayMs));
                    _muteTimer.Start();
                }
            }
        }

        private void OnMuteTimerTick(object sender, EventArgs e)
        {
            _muteTimer.Stop();
            if (!IsEnabledUser || Config == null) return;

            string currentFore = _foregroundTracker?.CurrentProcessName;
            bool isWhitelist = string.Equals(Config.Mode, "Whitelist", StringComparison.OrdinalIgnoreCase);

            if (isWhitelist)
            {
                // 白名单模式：静音除白名单应用及当前前台应用之外的所有活跃音频进程
                var activeProcs = _audioService?.GetActiveAudioProcesses() ?? new List<string>();

                foreach (var proc in activeProcs)
                {
                    if (!ProcessHelper.ContainsProcess(Config.TargetApps, proc) && !ProcessHelper.IsMatch(proc, currentFore))
                    {
                        _audioService?.SetProcessMute(proc, true);
                    }
                }

                // 兜底保障：静音操作执行后，确保当前前台绝不会处于静音状态
                if (!string.IsNullOrEmpty(currentFore))
                {
                    _audioService?.SetProcessMute(currentFore, false);
                }
            }
            else
            {
                // 黑名单模式：静音黑名单中且非当前前台的应用
                if (Config.TargetApps == null) return;
                foreach (var app in Config.TargetApps)
                {
                    if (!ProcessHelper.IsMatch(app, currentFore))
                    {
                        _audioService.SetProcessMute(app, true);
                    }
                }
            }
        }

        private void OnUnmuteTimerTick(object sender, EventArgs e)
        {
            _unmuteTimer.Stop();
            if (!IsEnabledUser) return;

            // 当前前台应用解除静音
            if (!string.IsNullOrEmpty(_lastTargetProc))
            {
                _audioService.SetProcessMute(_lastTargetProc, false);
            }
        }

        private void UnmuteAllTargets()
        {
            if (Config != null && Config.TargetApps != null)
            {
                _audioService.UnmuteProcesses(Config.TargetApps);
            }
            // 白名单模式下也解除当前所有活跃音频进程的静音
            if (Config != null && string.Equals(Config.Mode, "Whitelist", StringComparison.OrdinalIgnoreCase))
            {
                var activeProcs = _audioService.GetActiveAudioProcesses();
                _audioService.UnmuteProcesses(activeProcs);
            }
        }

        public override IEnumerable<TrayMenuItem> GetTrayMenuItems()
        {
            // 按模块二级聚合规范：本模块仅输出单一根节点，总开关与设置项收敛入二级菜单
            var items = new List<TrayMenuItem>();

            var root = new TrayMenuItem
            {
                Id = "appautomute_root",
                Header = BuildAutoMuteHeader(),
                ToolTip = Loc.T("Tray.AppAutoMute", "应用后台自动静音")
            };
            _trayRoot = root;

            // 1. 启用/禁用总开关
            _toggleItem = new TrayMenuItem
            {
                Id = "appautomute_toggle",
                Header = Loc.T("Tray.AutoMuteToggle", "启用后台自动静音"),
                InputGestureText = Config?.Hotkey ?? "Ctrl+Win+S",
                IsChecked = IsEnabledUser,
                ClickAction = () => ToggleEnabled()
            };
            root.Children.Add(_toggleItem);

            root.Children.Add(TrayMenuItem.Separator());

            // 2. 独立设置入口
            root.Children.Add(new TrayMenuItem
            {
                Id = "appautomute_settings",
                Header = Loc.T("Tray.AppAutoMuteSettings", "后台静音设置..."),
                ClickAction = () =>
                {
                    try
                    {
                        var cfgMgr = Context?.GetService<IConfigManager>();
                        var audio = Context?.GetService<IAudioService>();
                        var win = new CarroDesk.Views.AppAutoMuteSettingsWindow(this, cfgMgr, audio, msg => Context?.ShowNotification(msg))
                        {
                            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen
                        };
                        win.ShowDialog();
                        RequestRefreshSelf();
                    }
                    catch { }
                }
            });

            items.Add(root);
            return items;
        }

        private void RequestRefreshSelf()
        {
            try { Context?.RequestTrayRefresh(); } catch { }
        }
    }
}
