using System;
using System.Collections.Generic;
using System.Media;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Modules.AudioSwitch.Models;
using CarroDesk.Modules.AudioSwitch.Views;
using CarroDesk.Services.Localization;

namespace CarroDesk.Modules.AudioSwitch
{
    public class AudioSwitchModule : ModuleBase<AudioSwitchConfig>
    {
        public override string Id => "AudioSwitch";
        public override string Name => Loc.T("Audio.ModuleName", "音频输出设备切换");
        public override string Description => Loc.T("Audio.ModuleDesc", "快速在扬声器与耳机之间一键切换默认音频输出端点");

        private IAudioService _audioService;
        private IHotkeyService _hotkeys;
        private TrayMenuItem _trayItem;

        public AudioDeviceItem CurrentDefaultDevice { get; private set; }

        private AudioSwitchConfig _fallbackConfig;
        public new AudioSwitchConfig Config
        {
            get => base.Config ?? _fallbackConfig ?? (_fallbackConfig = new AudioSwitchConfig());
            internal set => _fallbackConfig = value;
        }

        public AudioSwitchModule()
        {
        }

        protected override void OnStart()
        {
            _audioService = Context.GetService<IAudioService>();
            _hotkeys = Context.GetService<IHotkeyService>();
            if (_audioService != null)
            {
                _audioService.DevicesChanged += OnDevicesChanged;
            }
            UpdateCurrentDevice();
            RegisterHotkey();
        }

        protected override void OnStop()
        {
            if (_audioService != null)
            {
                _audioService.DevicesChanged -= OnDevicesChanged;
            }
            UnregisterHotkey();
            _hotkeys?.UnregisterAll(Id);
        }

        private void OnDevicesChanged()
        {
            UpdateCurrentDevice();
            RequestRefreshSelf();
        }

        public override void OnConfigReloaded()
        {
            base.OnConfigReloaded();
            UnregisterHotkey();
            RegisterHotkey();
            UpdateCurrentDevice();
        }

        public override void OnLanguageChanged()
        {
            base.OnLanguageChanged();
            UpdateCurrentDevice();
        }

        private int _hotkeyId;

        private void RegisterHotkey()
        {
            if (Config == null || !Config.Enabled || string.IsNullOrEmpty(Config.Hotkey))
                return;

            try
            {
                _hotkeyId = _hotkeys.Register(Id, Config.Hotkey, () =>
                {
                    ToggleAudioDevice();
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

        public List<AudioDeviceItem> GetPlaybackDevices()
        {
            return _audioService?.GetPlaybackDevices() ?? new List<AudioDeviceItem>();
        }

        public bool SwitchToDevice(string deviceId)
        {
            if (_audioService == null || string.IsNullOrEmpty(deviceId)) return false;

            bool success = _audioService.SetDefaultPlaybackDevice(deviceId);
            if (success)
            {
                UpdateCurrentDevice();
                RequestRefreshSelf();

                if (Config != null && Config.PlayNotificationSound)
                {
                    try { SystemSounds.Asterisk.Play(); } catch { }
                }

                string devName = CurrentDefaultDevice != null ? CurrentDefaultDevice.Name : deviceId;
                string icon = "🔈";
                if (Config != null)
                {
                    if (devName.IndexOf(Config.HeadphonePattern ?? "耳机", StringComparison.OrdinalIgnoreCase) >= 0) icon = "🎧";
                    else if (devName.IndexOf(Config.SpeakerPattern ?? "扬声器", StringComparison.OrdinalIgnoreCase) >= 0) icon = "🔊";
                }
                string msg = Loc.T("Tray.AudioSwitchSwitched", "已切换音频输出至: {0} {1}", icon, devName);
                Context?.ShowNotification(msg);
                return true;
            }
            return false;
        }

        public bool IsDeviceExcluded(AudioDeviceItem device)
        {
            if (device == null) return false;
            return IsDeviceExcluded(device.Id, device.Name);
        }

        public bool IsDeviceExcluded(string id, string name)
        {
            if (Config?.ExcludedDevices == null || Config.ExcludedDevices.Count == 0)
                return false;

            foreach (var excl in Config.ExcludedDevices)
            {
                if (string.IsNullOrWhiteSpace(excl)) continue;
                string trimmed = excl.Trim();
                if (!string.IsNullOrEmpty(id) && string.Equals(trimmed, id, StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.IsNullOrEmpty(name) && string.Equals(trimmed, name, StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.IsNullOrEmpty(name) && name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        public bool ToggleAudioDevice()
        {
            if (_audioService == null) return false;

            UpdateCurrentDevice();
            var all = _audioService.GetPlaybackDevices();
            if (all == null || all.Count == 0)
            {
                Context?.ShowNotification(Loc.T("Audio.NoDevice", "未找到可用的音频输出设备"));
                return false;
            }

            // 过滤排除黑名单设备
            var available = all.FindAll(d => !IsDeviceExcluded(d));
            if (available.Count == 0)
            {
                // 若所有设备都被排除，降级回退到全量设备，防止无声死锁
                available = all;
            }

            if (available.Count == 1)
            {
                if (CurrentDefaultDevice != null && CurrentDefaultDevice.Id == available[0].Id)
                {
                    Context?.ShowNotification(Loc.T("Audio.OnlyOneDevice", "当前仅有一个可用的音频输出设备: {0}", available[0].Name));
                    return false;
                }
                else
                {
                    return SwitchToDevice(available[0].Id);
                }
            }

            string currentId = CurrentDefaultDevice?.Id;
            string currentName = CurrentDefaultDevice?.Name ?? string.Empty;

            string speakerPattern = Config?.SpeakerPattern ?? "扬声器";
            string headphonePattern = Config?.HeadphonePattern ?? "耳机";

            // 判断当前是耳机还是扬声器
            bool isHeadphone = currentName.IndexOf(headphonePattern, StringComparison.OrdinalIgnoreCase) >= 0;
            string targetPattern = isHeadphone ? speakerPattern : headphonePattern;

            AudioDeviceItem targetDevice = null;
            if (!string.IsNullOrEmpty(targetPattern))
            {
                targetDevice = available.Find(d => d.Id != currentId && !string.IsNullOrEmpty(d.Name) && d.Name.IndexOf(targetPattern, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            // 如果按 pattern 没找到目标，则在可用候选设备列表中循环切换至下一个设备 (Round-Robin)
            if (targetDevice == null)
            {
                int currentIndex = available.FindIndex(d => d.Id == currentId);
                int nextIndex = (currentIndex + 1) % available.Count;
                targetDevice = available[nextIndex];
            }

            if (targetDevice != null)
            {
                return SwitchToDevice(targetDevice.Id);
            }

            return false;
        }

        public void UpdateCurrentDevice()
        {
            try
            {
                CurrentDefaultDevice = _audioService?.GetDefaultPlaybackDevice();
                SetTrayItemSelf(BuildDeviceHeader(), BuildDeviceToolTip());
            }
            catch { }
        }

        public string GetDeviceShortName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return string.Empty;

            string speakerPattern = Config?.SpeakerPattern ?? "扬声器";
            string headphonePattern = Config?.HeadphonePattern ?? "耳机";

            // 1. 若匹配预设的耳机或扬声器关键字，优先返回标准精简类别名
            if (!string.IsNullOrEmpty(headphonePattern) &&
                fullName.IndexOf(headphonePattern, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return headphonePattern;
            }

            if (!string.IsNullOrEmpty(speakerPattern) &&
                fullName.IndexOf(speakerPattern, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return speakerPattern;
            }

            // 2. 常见英文回退（若设备名为英文 Speakers/Headphones）
            if (fullName.IndexOf("Headphone", StringComparison.OrdinalIgnoreCase) >= 0 ||
                fullName.IndexOf("Headset", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Headphones";
            }
            if (fullName.IndexOf("Speaker", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Speakers";
            }

            // 3. 其他设备（如外接显示器、USB 声卡、蓝牙设备等）：
            // 去除最外层括号中冗长的控制器/芯片驱动名称，如 "DELL U2720Q (NVIDIA High Definition Audio)" -> "DELL U2720Q"
            int parenIndex = fullName.IndexOf('(');
            string clean = (parenIndex > 0 ? fullName.Substring(0, parenIndex) : fullName).Trim();

            // 若仍过长，截断至最多 8 个字符加省略号，防止极端长名称撑宽主菜单
            if (clean.Length > 8)
            {
                clean = clean.Substring(0, 7) + "…";
            }

            return clean;
        }

        private string BuildDeviceHeader()
        {
            string devName = CurrentDefaultDevice?.Name;
            string baseTitle = Loc.T("Tray.AudioSwitch", "音频输出设备");
            if (string.IsNullOrEmpty(devName)) return baseTitle;

            string icon = GetDeviceIcon(devName);
            string shortName = GetDeviceShortName(devName);

            if (string.IsNullOrEmpty(shortName))
            {
                return $"{baseTitle} ({icon})";
            }
            return $"{baseTitle} ({icon} {shortName})";
        }

        private string BuildDeviceToolTip()
        {
            string devName = CurrentDefaultDevice?.Name;
            if (string.IsNullOrEmpty(devName)) return null;
            string icon = GetDeviceIcon(devName);
            return $"{Loc.T("Tray.AudioSwitch", "音频输出设备")}: {icon} {devName}";
        }

        private string GetDeviceIcon(string name)
        {
            if (string.IsNullOrEmpty(name)) return "🔈";
            if (Config != null)
            {
                if (name.IndexOf(Config.HeadphonePattern ?? "耳机", StringComparison.OrdinalIgnoreCase) >= 0) return "🎧";
                if (name.IndexOf(Config.SpeakerPattern ?? "扬声器", StringComparison.OrdinalIgnoreCase) >= 0) return "🔊";
            }
            return "🔈";
        }

        /// <summary>节点属性变更若在后台线程触发，模块自行 Dispatcher 封送回 UI（规范 §4.3）。</summary>
        private void SetTrayItemSelf(string header, string toolTip)
        {
            if (_trayItem == null) return;
            var d = Context?.Dispatcher;
            if (d != null && !d.CheckAccess())
            {
                d.BeginInvoke(new Action(() => SetTrayItemSelf(header, toolTip)));
                return;
            }
            _trayItem.Header = header;
            _trayItem.ToolTip = toolTip;
        }

        private void RequestRefreshSelf()
        {
            try { Context?.RequestTrayRefresh(); } catch { }
        }

        public override IEnumerable<TrayMenuItem> GetTrayMenuItems()
        {
            var items = new List<TrayMenuItem>();
            UpdateCurrentDevice();

            var root = new TrayMenuItem
            {
                Id = "audioswitch_root",
                Header = BuildDeviceHeader(),
                ToolTip = BuildDeviceToolTip()
            };
            _trayItem = root;

            root.Children.Add(new TrayMenuItem
            {
                Id = "audioswitch_fast_toggle",
                Header = Loc.T("Tray.FastToggle", "快捷切换"),
                InputGestureText = Config?.Hotkey ?? "Ctrl+`",
                ClickAction = () => { ToggleAudioDevice(); RequestRefreshSelf(); }
            });

            root.Children.Add(TrayMenuItem.Separator());

            string currentId = CurrentDefaultDevice?.Id;
            foreach (var d in GetPlaybackDevices())
            {
                if (d == null) continue;
                string name = d.Name ?? Loc.T("Audio.UnknownDevice", "(未知设备)");
                string icon = GetDeviceIcon(name);
                string devId = d.Id;
                bool isExcluded = IsDeviceExcluded(d);
                string header = isExcluded ? $"{icon} {name} " + Loc.T("Tray.AudioSwitchExcluded", "(已排除)") : $"{icon} {name}";
                root.Children.Add(new TrayMenuItem
                {
                    Id = "audioswitch_dev_" + devId,
                    Header = header,
                    IsChecked = string.Equals(d.Id, currentId, StringComparison.OrdinalIgnoreCase),
                    ClickAction = () => { SwitchToDevice(devId); RequestRefreshSelf(); }
                });
            }

            root.Children.Add(TrayMenuItem.Separator());

            root.Children.Add(new TrayMenuItem
            {
                Id = "audioswitch_settings",
                Header = Loc.T("Tray.AudioSwitchSettings", "音频切换设置..."),
                ClickAction = () =>
                {
                    try
                    {
                        var cfgMgr = Context?.GetService<IConfigManager>();
                        var win = new AudioSwitchSettingsWindow(this, cfgMgr, _audioService, msg => Context?.ShowNotification(msg))
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
    }
}
