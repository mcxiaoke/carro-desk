using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CarroDesk.Modules.MonitorProfile;
using CarroDesk.Modules.MonitorProfile.Models;

namespace CarroDesk.Views
{
    public partial class MonitorProfileSettingsWindow : Window
    {
        private readonly MonitorProfileModule _module;
        private readonly ObservableCollection<ModeItemViewModel> _modes = new ObservableCollection<ModeItemViewModel>();
        private readonly Dictionary<string, ObservableCollection<MonitorTimeSetting>> _modeSettings =
            new Dictionary<string, ObservableCollection<MonitorTimeSetting>>(StringComparer.OrdinalIgnoreCase);

        private string _activeProfileName = "Daily";

        public class ModeItemViewModel : INotifyPropertyChanged
        {
            private string _name;
            private bool _isActive;

            public string Name
            {
                get => _name;
                set { _name = value; OnPropertyChanged(nameof(Name)); }
            }

            public bool IsActive
            {
                get => _isActive;
                set { _isActive = value; OnPropertyChanged(nameof(IsActive)); }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        }

        public MonitorProfileSettingsWindow(MonitorProfileModule module)
        {
            InitializeComponent();
            _module = module ?? throw new ArgumentNullException(nameof(module));

            LstModes.ItemsSource = _modes;
            LoadConfigData();
            DetectHardwareAsync();
        }

        private void DetectHardwareAsync()
        {
            Task.Run(() =>
            {
                try
                {
                    var count = _module.DdcService.DetectMonitorCount();
                    var monitors = _module.DdcService.GetMonitorsInfo();
                    string desc = monitors.Count > 0
                        ? string.Join("; ", monitors.Select(m => $"{m.Description} (当前亮度:{m.CurrentBrightness}%)"))
                        : "未检测到外部 DDC/CI 显示器，已启用 WMI 适配";

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        TxtHardwareInfo.Text = $"检测到 {count} 台显示设备 | {desc}";
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        TxtHardwareInfo.Text = $"显示设备检测异常: {ex.Message}";
                    }));
                }
            });
        }

        private void LoadConfigData()
        {
            var cfg = _module.Config ?? MonitorProfileConfig.CreateDefault();

            _activeProfileName = cfg.ActiveProfile ?? "Daily";
            ChkAutoSchedule.IsChecked = cfg.AutoSchedule;

            // 步长
            int step = cfg.BrightnessStep > 0 ? cfg.BrightnessStep : 5;
            foreach (ComboBoxItem item in CmbBrightnessStep.Items)
            {
                if (item.Tag != null && int.TryParse(item.Tag.ToString(), out int val) && val == step)
                {
                    item.IsSelected = true;
                    break;
                }
            }

            // 快捷键
            var hk = cfg.Hotkeys ?? new MonitorHotkeyConfig();
            TxtHkDaily.Text = hk.SwitchToDailyMode ?? "Ctrl+Shift+D";
            TxtHkGame.Text = hk.SwitchToGameMode ?? "Ctrl+Shift+G";
            TxtHkNight.Text = hk.SwitchToNightMode ?? "Ctrl+Shift+N";
            TxtHkUp.Text = hk.IncreaseBrightness ?? "Ctrl+Shift+Up";
            TxtHkDown.Text = hk.DecreaseBrightness ?? "Ctrl+Shift+Down";
            TxtHkRefresh.Text = hk.ManualRefresh ?? "Ctrl+Shift+R";

            // 模式列表
            _modes.Clear();
            _modeSettings.Clear();

            var profiles = cfg.Profiles != null && cfg.Profiles.Count > 0
                ? cfg.Profiles
                : MonitorProfileConfig.CreateDefault().Profiles;

            foreach (var kvp in profiles)
            {
                string modeName = kvp.Key;
                bool isAct = string.Equals(modeName, _activeProfileName, StringComparison.OrdinalIgnoreCase);
                _modes.Add(new ModeItemViewModel { Name = modeName, IsActive = isAct });

                var timeList = new ObservableCollection<MonitorTimeSetting>();
                if (kvp.Value != null)
                {
                    foreach (var ts in kvp.Value.OrderBy(t => t.ToTimeSpan()))
                    {
                        timeList.Add(ts.Clone());
                    }
                }
                _modeSettings[modeName] = timeList;
            }

            var targetSelect = _modes.FirstOrDefault(m => string.Equals(m.Name, _activeProfileName, StringComparison.OrdinalIgnoreCase))
                               ?? _modes.FirstOrDefault();
            if (targetSelect != null)
            {
                LstModes.SelectedItem = targetSelect;
            }
        }

        private void LstModes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = LstModes.SelectedItem as ModeItemViewModel;
            if (selected == null || !_modeSettings.ContainsKey(selected.Name))
            {
                GridTimeSettings.ItemsSource = null;
                TxtCurrentModeTitle.Text = "请选择一个情境模式";
                return;
            }

            TxtCurrentModeTitle.Text = $"模式「{selected.Name}」的时间段设置";
            GridTimeSettings.ItemsSource = _modeSettings[selected.Name];
        }

        private void BtnSetActive_Click(object sender, RoutedEventArgs e)
        {
            var selected = LstModes.SelectedItem as ModeItemViewModel;
            if (selected == null) return;

            _activeProfileName = selected.Name;
            foreach (var m in _modes)
            {
                m.IsActive = string.Equals(m.Name, _activeProfileName, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void BtnAddMode_Click(object sender, RoutedEventArgs e)
        {
            string baseName = "Custom";
            int idx = 1;
            while (_modes.Any(m => string.Equals(m.Name, $"{baseName}{idx}", StringComparison.OrdinalIgnoreCase)))
            {
                idx++;
            }
            string newName = $"{baseName}{idx}";

            var newMode = new ModeItemViewModel { Name = newName, IsActive = false };
            _modes.Add(newMode);
            _modeSettings[newName] = new ObservableCollection<MonitorTimeSetting>
            {
                new MonitorTimeSetting { Time = "08:00", Brightness = 60, Contrast = 70 },
                new MonitorTimeSetting { Time = "20:00", Brightness = 40, Contrast = 65 }
            };

            LstModes.SelectedItem = newMode;
        }

        private void BtnDeleteMode_Click(object sender, RoutedEventArgs e)
        {
            if (_modes.Count <= 1)
            {
                MessageBox.Show("至少需要保留一个情境模式。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var selected = LstModes.SelectedItem as ModeItemViewModel;
            if (selected == null) return;

            string name = selected.Name;
            _modes.Remove(selected);
            _modeSettings.Remove(name);

            if (string.Equals(name, _activeProfileName, StringComparison.OrdinalIgnoreCase))
            {
                var fallback = _modes.First();
                _activeProfileName = fallback.Name;
                fallback.IsActive = true;
            }

            LstModes.SelectedItem = _modes.First();
        }

        private void BtnAddTimeSetting_Click(object sender, RoutedEventArgs e)
        {
            var selected = LstModes.SelectedItem as ModeItemViewModel;
            if (selected == null || !_modeSettings.ContainsKey(selected.Name)) return;

            string timeStr = TxtNewTime.Text.Trim();
            if (!TimeSpan.TryParse(timeStr, out _))
            {
                MessageBox.Show("请输入正确的时间格式 (HH:mm)，例如 18:30", "格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int b, c;
            if (!int.TryParse(TxtNewBrightness.Text, out b) || b < 0 || b > 100)
            {
                MessageBox.Show("亮度值必须在 0 到 100 之间。", "格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtNewContrast.Text, out c) || c < 0 || c > 100)
            {
                MessageBox.Show("对比度值必须在 0 到 100 之间。", "格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var list = _modeSettings[selected.Name];
            list.Add(new MonitorTimeSetting { Time = timeStr, Brightness = b, Contrast = c });

            // 重新按时间排序
            var sorted = list.OrderBy(t => t.ToTimeSpan()).ToList();
            list.Clear();
            foreach (var item in sorted)
            {
                list.Add(item);
            }
        }

        private void BtnDeleteTimeSetting_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var setting = btn?.DataContext as MonitorTimeSetting;
            var selected = LstModes.SelectedItem as ModeItemViewModel;

            if (setting != null && selected != null && _modeSettings.ContainsKey(selected.Name))
            {
                var list = _modeSettings[selected.Name];
                if (list.Count <= 1)
                {
                    MessageBox.Show("每个模式至少需要保留一个时间点。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                list.Remove(setting);
            }
        }

        private void BtnResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确认恢复到默认显示器情境与时间表吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var def = MonitorProfileConfig.CreateDefault();
                _module.SaveAndApplyConfig(def);
                LoadConfigData();
            }
        }

        private void BtnTestApply_Click(object sender, RoutedEventArgs e)
        {
            var cfg = BuildCurrentConfigFromUi();
            _module.SaveAndApplyConfig(cfg);
            _module.ScheduleEngine.ApplyCurrentSetting(force: true);
            MessageBox.Show($"已应用测试：当前情境 [{cfg.ActiveProfile}]", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var cfg = BuildCurrentConfigFromUi();
            _module.SaveAndApplyConfig(cfg);
            Close();
        }

        private MonitorProfileConfig BuildCurrentConfigFromUi()
        {
            var cfg = new MonitorProfileConfig();

            cfg.ActiveProfile = _activeProfileName;
            cfg.AutoSchedule = ChkAutoSchedule.IsChecked == true;

            var selItem = CmbBrightnessStep.SelectedItem as ComboBoxItem;
            if (selItem?.Tag != null && int.TryParse(selItem.Tag.ToString(), out int step))
            {
                cfg.BrightnessStep = step;
            }

            if (cfg.Hotkeys == null) cfg.Hotkeys = new MonitorHotkeyConfig();
            cfg.Hotkeys.SwitchToDailyMode = TxtHkDaily.Text.Trim();
            cfg.Hotkeys.SwitchToGameMode = TxtHkGame.Text.Trim();
            cfg.Hotkeys.SwitchToNightMode = TxtHkNight.Text.Trim();
            cfg.Hotkeys.IncreaseBrightness = TxtHkUp.Text.Trim();
            cfg.Hotkeys.DecreaseBrightness = TxtHkDown.Text.Trim();
            cfg.Hotkeys.ManualRefresh = TxtHkRefresh.Text.Trim();

            cfg.Profiles.Clear();
            foreach (var kvp in _modeSettings)
            {
                cfg.Profiles[kvp.Key] = kvp.Value.ToList();
            }

            return cfg;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
