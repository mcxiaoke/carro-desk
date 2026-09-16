using System;
using System.Collections.Generic;
using System.Media;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Host.Services;
using CarroDesk.Modules.AudioSwitch.Models;
using ScreenLock.Services.Tasks;

namespace CarroDesk.Modules.AudioSwitch
{
    public class AudioSwitchModule : ModuleBase<AudioSwitchConfig>
    {
        public static AudioSwitchModule Instance { get; private set; }

        public override string Id => "AudioSwitch";
        public override string Name => "音频输出设备切换";
        public override string Description => "快速在扬声器与耳机之间一键切换默认音频输出端点";

        private readonly AudioService _audioService;
        private TrayMenuItem _trayItem;

        public AudioDeviceItem CurrentDefaultDevice { get; private set; }
        public Action<string> NotificationCallback { get; set; }

        public AudioSwitchModule(AudioService audioService)
        {
            Instance = this;
            _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
        }

        protected override void OnStart()
        {
            UpdateCurrentDevice();
            RegisterHotkey();
        }

        protected override void OnStop()
        {
            UnregisterHotkey();
        }

        public override void OnConfigReloaded()
        {
            base.OnConfigReloaded();
            UnregisterHotkey();
            RegisterHotkey();
            UpdateCurrentDevice();
        }

        private int _hotkeyId;

        private void RegisterHotkey()
        {
            if (Config == null || !Config.Enabled || string.IsNullOrEmpty(Config.Hotkey))
                return;

            try
            {
                _hotkeyId = HotkeyService.Instance.Register(Config.Hotkey, () =>
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
                    HotkeyService.Instance.Unregister(_hotkeyId);
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
                string msg = $"已切换音频输出至: {icon} {devName}";
                NotificationCallback?.Invoke(msg);
                return true;
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
                NotificationCallback?.Invoke("未找到可用的音频输出设备");
                return false;
            }

            if (all.Count == 1)
            {
                NotificationCallback?.Invoke($"当前仅有一个音频输出设备: {all[0].Name}");
                return false;
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
                targetDevice = _audioService.FindDeviceByPattern(targetPattern);
                // 如果找到的目标刚好是当前设备，则不视为目标
                if (targetDevice != null && targetDevice.Id == currentId)
                {
                    targetDevice = null;
                }
            }

            // 如果按 pattern 没找到目标，则在活跃设备列表中循环切换至下一个设备 (Round-Robin)
            if (targetDevice == null)
            {
                int currentIndex = all.FindIndex(d => d.Id == currentId);
                int nextIndex = (currentIndex + 1) % all.Count;
                targetDevice = all[nextIndex];
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
                CurrentDefaultDevice = _audioService.GetDefaultPlaybackDevice();
                if (_trayItem != null)
                {
                    string icon = "🔈";
                    string devName = CurrentDefaultDevice != null ? CurrentDefaultDevice.Name : "未检测到设备";
                    if (Config != null)
                    {
                        if (devName.IndexOf(Config.HeadphonePattern ?? "耳机", StringComparison.OrdinalIgnoreCase) >= 0) icon = "🎧";
                        else if (devName.IndexOf(Config.SpeakerPattern ?? "扬声器", StringComparison.OrdinalIgnoreCase) >= 0) icon = "🔊";
                    }
                    _trayItem.Header = $"切换音频设备 (当前: {icon} {devName})";
                }
            }
            catch { }
        }

        public override IEnumerable<TrayMenuItem> GetTrayMenuItems()
        {
            var items = new List<TrayMenuItem>();

            _trayItem = new TrayMenuItem
            {
                Id = "audioswitch_toggle",
                Header = "切换音频设备",
                InputGestureText = Config?.Hotkey ?? "Ctrl+`",
                ClickAction = () => ToggleAudioDevice()
            };

            UpdateCurrentDevice();
            items.Add(_trayItem);

            return items;
        }
    }
}
