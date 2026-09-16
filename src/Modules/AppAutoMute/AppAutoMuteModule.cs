using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Host.Services;
using CarroDesk.Modules.AppAutoMute.Models;
using ScreenLock.Services.Tasks;

namespace CarroDesk.Modules.AppAutoMute
{
    public class AppAutoMuteModule : ModuleBase<AppAutoMuteConfig>
    {
        public static AppAutoMuteModule Instance { get; private set; }

        public override string Id => "AppAutoMute";
        public override string Name => "应用前后台智能静音";
        public override string Description => "当目标应用位于后台时自动静音，切回前台时自动恢复发声";

        private readonly AudioService _audioService;
        private readonly ForegroundTracker _foregroundTracker;

        private readonly DispatcherTimer _muteTimer;
        private readonly DispatcherTimer _unmuteTimer;

        private string _lastTargetProc;
        private TrayMenuItem _trayItem;

        public bool IsEnabledUser => Config != null && Config.Enabled;

        public Action<string> NotificationCallback { get; set; }

        public AppAutoMuteModule(AudioService audioService, ForegroundTracker foregroundTracker)
        {
            Instance = this;
            _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
            _foregroundTracker = foregroundTracker ?? throw new ArgumentNullException(nameof(foregroundTracker));

            _muteTimer = new DispatcherTimer();
            _muteTimer.Tick += OnMuteTimerTick;

            _unmuteTimer = new DispatcherTimer();
            _unmuteTimer.Tick += OnUnmuteTimerTick;
        }

        protected override void OnStart()
        {
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

            // 核心安全保护：停用时强制全量解除目标应用静音
            UnmuteAllTargets();
        }

        public override void OnConfigReloaded()
        {
            base.OnConfigReloaded();
            UnregisterHotkey();
            RegisterHotkey();
            if (_trayItem != null)
            {
                _trayItem.IsChecked = IsEnabledUser;
            }
        }

        private int _hotkeyId;

        private void RegisterHotkey()
        {
            if (Config == null || string.IsNullOrEmpty(Config.Hotkey))
                return;

            try
            {
                _hotkeyId = HotkeyService.Instance.Register(Config.Hotkey, () =>
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
                    HotkeyService.Instance.Unregister(_hotkeyId);
                    _hotkeyId = 0;
                }
                catch { }
            }
        }

        public void ToggleEnabled()
        {
            if (Config == null) return;
            Config.Enabled = !Config.Enabled;

            if (_trayItem != null)
            {
                _trayItem.IsChecked = Config.Enabled;
            }

            var configMgr = Services?.GetService<IConfigManager>();
            configMgr?.SaveModuleConfig(Id, Config);

            string msg = Config.Enabled ? "应用自动静音已启用" : "应用自动静音已禁用 (已恢复所有声音)";
            NotificationCallback?.Invoke(msg);

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

        private void OnForegroundChanged(IntPtr hwnd, string procName)
        {
            if (!IsEnabledUser) return;
            EvaluateForeground(procName);
        }

        private void EvaluateForeground(string procName)
        {
            if (string.IsNullOrEmpty(procName) || Config == null || Config.TargetApps == null || Config.TargetApps.Count == 0)
                return;

            bool isTarget = Config.TargetApps.Any(app => string.Equals(app, procName, StringComparison.OrdinalIgnoreCase));

            if (isTarget)
            {
                // 前台是目标应用：停止静音计时，启动恢复声音计时
                _muteTimer.Stop();
                _lastTargetProc = procName;
                _unmuteTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, Config.UnmuteDelayMs));
                _unmuteTimer.Start();
            }
            else
            {
                // 前台切出到其他非目标应用：停止恢复计时，启动静音计时
                _unmuteTimer.Stop();
                _muteTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, Config.MuteDelayMs));
                _muteTimer.Start();
            }
        }

        private void OnMuteTimerTick(object sender, EventArgs e)
        {
            _muteTimer.Stop();
            if (!IsEnabledUser || Config == null || Config.TargetApps == null) return;

            // 遍历所有目标应用，将当前非前台的目标应用静音
            string currentFore = _foregroundTracker.CurrentProcessName;
            foreach (var app in Config.TargetApps)
            {
                if (!string.Equals(app, currentFore, StringComparison.OrdinalIgnoreCase))
                {
                    _audioService.SetProcessMute(app, true);
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
        }

        public override IEnumerable<TrayMenuItem> GetTrayMenuItems()
        {
            var items = new List<TrayMenuItem>();

            _trayItem = new TrayMenuItem
            {
                Id = "appautomute_toggle",
                Header = "应用后台自动静音",
                InputGestureText = Config?.Hotkey ?? "Ctrl+Win+S",
                IsChecked = IsEnabledUser,
                ClickAction = () => ToggleEnabled()
            };

            items.Add(_trayItem);
            return items;
        }
    }
}
