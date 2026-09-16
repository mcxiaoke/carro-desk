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

        public bool ToggleAudioDevice()
        {
            if (_audioService == null) return false;

            UpdateCurrentDevice();
            string currentName = CurrentDefaultDevice != null ? CurrentDefaultDevice.Name : string.Empty;

            string speakerPattern = Config?.SpeakerPattern ?? "扬声器";
            string headphonePattern = Config?.HeadphonePattern ?? "耳机";

            // 判断当前是耳机还是扬声器
            bool isHeadphone = currentName.IndexOf(headphonePattern, StringComparison.OrdinalIgnoreCase) >= 0;
            string targetPattern = isHeadphone ? speakerPattern : headphonePattern;

            var targetDevice = _audioService.FindDeviceByPattern(targetPattern);
            if (targetDevice == null)
            {
                // 如果找不到目标，尝试反向或切换到任一其他设备
                var all = _audioService.GetPlaybackDevices();
                foreach (var d in all)
                {
                    if (CurrentDefaultDevice == null || d.Id != CurrentDefaultDevice.Id)
                    {
                        targetDevice = d;
                        break;
                    }
                }
            }

            if (targetDevice != null)
            {
                bool success = _audioService.SetDefaultPlaybackDevice(targetDevice.Id);
                if (success)
                {
                    UpdateCurrentDevice();

                    if (Config != null && Config.PlayNotificationSound)
                    {
                        try { SystemSounds.Asterisk.Play(); } catch { }
                    }

                    string icon = targetDevice.Name.IndexOf(headphonePattern, StringComparison.OrdinalIgnoreCase) >= 0 ? "🎧" : "🔊";
                    string msg = $"已切换音频输出至: {icon} {targetDevice.Name}";
                    NotificationCallback?.Invoke(msg);

                    return true;
                }
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
