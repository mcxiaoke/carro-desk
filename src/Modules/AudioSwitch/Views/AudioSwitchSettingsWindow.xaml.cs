using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Modules.AudioSwitch;
using CarroDesk.Modules.AudioSwitch.Models;
using CarroDesk.Services.Tasks;
using CarroDesk.Services.Localization;

namespace CarroDesk.Modules.AudioSwitch.Views
{
    public class AudioDeviceViewModel : INotifyPropertyChanged
    {
        private static readonly Brush GreenBg;
        private static readonly Brush GreenFg;
        private static readonly Brush AmberBg;
        private static readonly Brush AmberFg;
        private static readonly Brush GrayBg;
        private static readonly Brush GrayFg;

        static AudioDeviceViewModel()
        {
            GreenBg = new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7));
            GreenBg.Freeze();
            GreenFg = new SolidColorBrush(Color.FromRgb(0x16, 0x65, 0x34));
            GreenFg.Freeze();

            AmberBg = new SolidColorBrush(Color.FromRgb(0xFE, 0xF3, 0xC7));
            AmberBg.Freeze();
            AmberFg = new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E));
            AmberFg.Freeze();

            GrayBg = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
            GrayBg.Freeze();
            GrayFg = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
            GrayFg.Freeze();
        }

        private bool _isExcluded;
        private bool _isCurrentDefault;

        public string Id { get; set; }
        public string Name { get; set; }
        public string Icon { get; set; }

        public bool IsExcluded
        {
            get => _isExcluded;
            set
            {
                if (_isExcluded != value)
                {
                    _isExcluded = value;
                    OnPropertyChanged(nameof(IsExcluded));
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(BadgeBackground));
                    OnPropertyChanged(nameof(BadgeForeground));
                }
            }
        }

        public bool IsCurrentDefault
        {
            get => _isCurrentDefault;
            set
            {
                if (_isCurrentDefault != value)
                {
                    _isCurrentDefault = value;
                    OnPropertyChanged(nameof(IsCurrentDefault));
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(BadgeBackground));
                    OnPropertyChanged(nameof(BadgeForeground));
                }
            }
        }

        public string StatusText
        {
            get
            {
                if (IsCurrentDefault) return Loc.T("Audio.StatusCurrentDefault", "当前默认");
                if (IsExcluded) return Loc.T("Audio.StatusExcluded", "已排除");
                return Loc.T("Audio.StatusReady", "就绪");
            }
        }

        public Brush BadgeBackground
        {
            get
            {
                if (IsCurrentDefault) return GreenBg;
                if (IsExcluded) return AmberBg;
                return GrayBg;
            }
        }

        public Brush BadgeForeground
        {
            get
            {
                if (IsCurrentDefault) return GreenFg;
                if (IsExcluded) return AmberFg;
                return GrayFg;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public partial class AudioSwitchSettingsWindow : Window
    {
        private readonly ObservableCollection<AudioDeviceViewModel> _devices = new ObservableCollection<AudioDeviceViewModel>();
        private readonly AudioSwitchModule _module;
        private readonly IConfigManager _configManager;
        private readonly IAudioService _audioService;
        private readonly Action<string> _notifier;
        private AudioSwitchConfig _initialSnapshot;
        private readonly List<string> _offlineExclusions = new List<string>();
        private bool _commitChanges;

        public AudioSwitchSettingsWindow(
            AudioSwitchModule module = null,
            IConfigManager configManager = null,
            IAudioService audioService = null,
            Action<string> notifier = null)
        {
            _module = module;
            _configManager = configManager;
            _audioService = audioService;
            _notifier = notifier;

            InitializeComponent();
            Closing += OnWindowClosing;
            LstDevices.ItemsSource = _devices;
            LoadCurrentSettings();
        }

        private void LoadCurrentSettings()
        {
            var config = _module?.Config ?? (_configManager != null ? _configManager.GetModuleConfig<AudioSwitchConfig>("AudioSwitch") : new AudioSwitchConfig());

            _initialSnapshot = config.Clone();
            _offlineExclusions.Clear();
            if (config.ExcludedDevices != null) _offlineExclusions.AddRange(config.ExcludedDevices);
            ChkEnabled.IsChecked = config.Enabled;
            TxtHotkey.Text = config.Hotkey ?? "Ctrl+`";
            ChkPlaySound.IsChecked = config.PlayNotificationSound;
            TxtSpeakerPattern.Text = config.SpeakerPattern ?? Loc.T("Audio.Speaker", "扬声器");
            TxtHeadphonePattern.Text = config.HeadphonePattern ?? Loc.T("Audio.Headset", "耳机");

            LoadAudioDevices(config.ExcludedDevices);
        }

        private void LoadAudioDevices(List<string> excludedList = null)
        {
            _devices.Clear();

            var excludedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (excludedList != null)
            {
                foreach (var item in excludedList)
                {
                    if (!string.IsNullOrWhiteSpace(item)) excludedSet.Add(item.Trim());
                }
            }

            var audioSvc = _audioService;
            var currentDefault = audioSvc?.GetDefaultPlaybackDevice() ?? _module?.CurrentDefaultDevice;
            string currentDefaultId = currentDefault?.Id;

            // 更新顶部默认设备徽章
            if (currentDefault != null)
            {
                string icon = GetDeviceIcon(currentDefault.Name);
                StatusBadgeText.Text = Loc.T("Audio.CurrentDefault", "当前默认: {0} {1}", icon, currentDefault.Name);
            }
            else
            {
                StatusBadgeText.Text = Loc.T("Audio.CurrentDefaultNone", "当前默认: (未检测到)");
            }

            var rawDevices = audioSvc?.GetPlaybackDevices() ?? _module?.GetPlaybackDevices() ?? new List<AudioDeviceItem>();
            foreach (var d in rawDevices)
            {
                if (d == null) continue;
                string name = d.Name ?? Loc.T("Audio.UnknownDevice", "(未知设备)");
                string icon = GetDeviceIcon(name);

                bool isExcluded = false;
                if (excludedSet.Contains(d.Id) || excludedSet.Contains(name))
                {
                    isExcluded = true;
                }
                else
                {
                    foreach (var excl in excludedSet)
                    {
                        if (name.IndexOf(excl, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            isExcluded = true;
                            break;
                        }
                    }
                }

                bool isDefault = !string.IsNullOrEmpty(currentDefaultId) && string.Equals(d.Id, currentDefaultId, StringComparison.OrdinalIgnoreCase);

                _devices.Add(new AudioDeviceViewModel
                {
                    Id = d.Id,
                    Name = name,
                    Icon = icon,
                    IsCurrentDefault = isDefault,
                    IsExcluded = isExcluded
                });
            }

            TxtDeviceCount.Text = Loc.T("Audio.DeviceListTitle", "系统音频播放设备与排除策略 (共 {0} 个):", _devices.Count);
        }

        private string GetDeviceIcon(string name)
        {
            if (string.IsNullOrEmpty(name)) return "🔈";
            string sp = TxtSpeakerPattern?.Text?.Trim() ?? Loc.T("Audio.Speaker", "扬声器");
            string hp = TxtHeadphonePattern?.Text?.Trim() ?? Loc.T("Audio.Headset", "耳机");

            if (!string.IsNullOrEmpty(hp) && name.IndexOf(hp, StringComparison.OrdinalIgnoreCase) >= 0) return "🎧";
            if (!string.IsNullOrEmpty(sp) && name.IndexOf(sp, StringComparison.OrdinalIgnoreCase) >= 0) return "🔊";
            return "🔈";
        }

        private void OnSetAsDefaultClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AudioDeviceViewModel vm)
            {
                SwitchDeviceTo(vm);
            }
        }

        private void OnDeviceMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstDevices.SelectedItem is AudioDeviceViewModel vm)
            {
                SwitchDeviceTo(vm);
            }
        }

        private void SwitchDeviceTo(AudioDeviceViewModel vm)
        {
            if (vm == null || string.IsNullOrEmpty(vm.Id)) return;

            bool ok = false;
            if (_module != null)
            {
                ok = _module.SwitchToDevice(vm.Id);
            }
            else if (_audioService != null)
            {
                ok = _audioService.SetDefaultPlaybackDevice(vm.Id);
            }

            if (ok)
            {
                foreach (var d in _devices)
                {
                    d.IsCurrentDefault = string.Equals(d.Id, vm.Id, StringComparison.OrdinalIgnoreCase);
                }
                StatusBadgeText.Text = Loc.T("Audio.CurrentDefault", "当前默认: {0} {1}", vm.Icon, vm.Name);
                TxtNotice.Text = Loc.T("Audio.SwitchedTo", "已切换至默认: {0}", vm.Name);
            }
            else
            {
                MessageBox.Show(this, Loc.T("Audio.SwitchFailed", "切换至设备【{0}】失败，请检查设备是否连接正常。", vm.Name), Loc.T("Audio.SwitchFailedTitle", "切换失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnUseSelectedAsSpeakerClick(object sender, RoutedEventArgs e)
        {
            if (LstDevices.SelectedItem is AudioDeviceViewModel vm)
            {
                string clean = CleanDeviceNameForPattern(vm.Name);
                TxtSpeakerPattern.Text = clean;
                TxtNotice.Text = Loc.T("Audio.SetSpeakerKeyword", "已将【{0}】设为扬声器匹配关键字", clean);
            }
            else
            {
                MessageBox.Show(this, Loc.T("Audio.SelectDeviceFirst", "请先在下方列表中选择一个音频设备。"), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnUseSelectedAsHeadphoneClick(object sender, RoutedEventArgs e)
        {
            if (LstDevices.SelectedItem is AudioDeviceViewModel vm)
            {
                string clean = CleanDeviceNameForPattern(vm.Name);
                TxtHeadphonePattern.Text = clean;
                TxtNotice.Text = Loc.T("Audio.SetHeadsetKeyword", "已将【{0}】设为耳机匹配关键字", clean);
            }
            else
            {
                MessageBox.Show(this, Loc.T("Audio.SelectDeviceFirst", "请先在下方列表中选择一个音频设备。"), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private string CleanDeviceNameForPattern(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return string.Empty;
            int paren = fullName.IndexOf('(');
            string baseName = paren > 0 ? fullName.Substring(0, paren).Trim() : fullName.Trim();
            return string.IsNullOrEmpty(baseName) ? fullName : baseName;
        }

        private void OnSetDefaultMenuItemClick(object sender, RoutedEventArgs e)
        {
            if (LstDevices.SelectedItem is AudioDeviceViewModel vm)
            {
                SwitchDeviceTo(vm);
            }
        }

        private void OnToggleExclusionMenuItemClick(object sender, RoutedEventArgs e)
        {
            if (LstDevices.SelectedItem is AudioDeviceViewModel vm)
            {
                vm.IsExcluded = !vm.IsExcluded;
            }
        }

        private void OnRefreshDevicesClick(object sender, RoutedEventArgs e)
        {
            var currentExclusions = _devices.Where(x => x.IsExcluded).Select(x => x.Name)
                .Concat(_offlineExclusions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            LoadAudioDevices(currentExclusions);
            TxtNotice.Text = Loc.T("Audio.Refreshed", "已刷新音频播放设备列表");
        }

        private void OnClearExclusionsClick(object sender, RoutedEventArgs e)
        {
            if (_devices.Count == 0) return;
            foreach (var d in _devices)
            {
                d.IsExcluded = false;
            }
            TxtNotice.Text = Loc.T("Audio.ClearedExclusions", "已清除所有排除项");
        }

        private void OnTestToggleClick(object sender, RoutedEventArgs e)
        {
            if (_module != null)
            {
                // 先同步界面配置到 module.Config 以便测试体验真实配置
                if (_module.Config != null)
                {
                    _module.Config.SpeakerPattern = TxtSpeakerPattern.Text.Trim();
                    _module.Config.HeadphonePattern = TxtHeadphonePattern.Text.Trim();
                    _module.Config.ExcludedDevices = _devices.Where(x => x.IsExcluded).Select(x => x.Name)
                        .Concat(_offlineExclusions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    _module.Config.PlayNotificationSound = ChkPlaySound.IsChecked == true;
                }

                bool switched = _module.ToggleAudioDevice();
                if (switched)
                {
                    string curId = _module.CurrentDefaultDevice?.Id;
                    foreach (var d in _devices)
                    {
                        d.IsCurrentDefault = string.Equals(d.Id, curId, StringComparison.OrdinalIgnoreCase);
                    }
                    if (_module.CurrentDefaultDevice != null)
                    {
                        string icon = GetDeviceIcon(_module.CurrentDefaultDevice.Name);
                        StatusBadgeText.Text = Loc.T("Audio.CurrentDefault", "当前默认: {0} {1}", icon, _module.CurrentDefaultDevice.Name);
                    }
                    TxtNotice.Text = Loc.T("Audio.QuickSwitchOk", "快捷切换成功！");
                }
                else
                {
                    TxtNotice.Text = Loc.T("Audio.QuickSwitchSkipped", "未切换 (可能仅有一个可用设备)");
                }
            }
            else
            {
                TxtNotice.Text = Loc.T("Audio.ModuleNotMounted", "模块未挂载，无法测试切换");
            }
        }

        private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show(this, Loc.T("Audio.RestoreConfirm", "确定要恢复音频切换模块的默认配置吗？"), Loc.T("Common.ResetDefaults", "恢复默认"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                ChkEnabled.IsChecked = true;
                TxtHotkey.Text = "Ctrl+`";
                ChkPlaySound.IsChecked = true;
                TxtSpeakerPattern.Text = Loc.T("Audio.Speaker", "扬声器");
                TxtHeadphonePattern.Text = Loc.T("Audio.Headset", "耳机");
                foreach (var d in _devices)
                {
                    d.IsExcluded = false;
                }
                TxtNotice.Text = Loc.T("Audio.Restored", "已恢复为默认配置");
            }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            string hotkey = TxtHotkey.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(hotkey))
            {
                if (!HotkeyHelper.Validate(hotkey, out string err))
                {
                    MessageBox.Show(this, Loc.T("Msg.HotkeyInvalid", "快捷键格式不正确: {0}\n支持格式例如: {1}", err, "Ctrl+`, Alt+F11, Win+Ctrl+A"), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var configMgr = _configManager;
            var config = _module?.Config ?? (configMgr != null ? configMgr.GetModuleConfig<AudioSwitchConfig>("AudioSwitch") : new AudioSwitchConfig());
            var previous = config.Clone();

            config.Enabled = ChkEnabled.IsChecked == true;
            config.Hotkey = hotkey;
            config.PlayNotificationSound = ChkPlaySound.IsChecked == true;
            config.SpeakerPattern = TxtSpeakerPattern.Text.Trim();
            config.HeadphonePattern = TxtHeadphonePattern.Text.Trim();
            config.ExcludedDevices = _devices.Where(x => x.IsExcluded).Select(x => x.Name)
                .Concat(_offlineExclusions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (configMgr == null || !configMgr.SaveModuleConfig("AudioSwitch", config))
            {
                config.Enabled = previous.Enabled;
                config.Hotkey = previous.Hotkey;
                config.SpeakerPattern = previous.SpeakerPattern;
                config.HeadphonePattern = previous.HeadphonePattern;
                config.PlayNotificationSound = previous.PlayNotificationSound;
                config.ExcludedDevices = previous.ExcludedDevices;
                MessageBox.Show(this, Loc.T("Config.SaveFailed"), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _module?.OnConfigReloaded();
            _commitChanges = true;
            _module?.LogInfo("音频输出设备切换配置已保存并生效");

            DialogResult = true;
            Close();
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (_commitChanges || _module?.Config == null || _initialSnapshot == null) return;
            var config = _module.Config;
            config.Enabled = _initialSnapshot.Enabled;
            config.Hotkey = _initialSnapshot.Hotkey;
            config.SpeakerPattern = _initialSnapshot.SpeakerPattern;
            config.HeadphonePattern = _initialSnapshot.HeadphonePattern;
            config.PlayNotificationSound = _initialSnapshot.PlayNotificationSound;
            config.ExcludedDevices = _initialSnapshot.ExcludedDevices;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
