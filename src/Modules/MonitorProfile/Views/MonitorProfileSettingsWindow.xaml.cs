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
using CarroDesk.Services.Localization;

namespace CarroDesk.Modules.MonitorProfile.Views
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
                        ? string.Join("; ", monitors.Select(m => Loc.T("Monitor.BrightnessSuffix", "{0} (当前亮度:{1}%)", m.Description, m.CurrentBrightness)))
                        : Loc.T("Monitor.NoDdcMonitor", "未检测到外部 DDC/CI 显示器，已启用 WMI 适配");

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        TxtHardwareInfo.Text = Loc.T("Monitor.DetectedCount", "检测到 {0} 台显示设备 | {1}", count, desc);
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        TxtHardwareInfo.Text = Loc.T("Monitor.DetectFailed", "显示设备检测异常: {0}", ex.Message);
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
                TxtCurrentModeTitle.Text = Loc.T("Monitor.SelectProfile", "请选择一个情境模式");
                return;
            }

            TxtCurrentModeTitle.Text = Loc.T("Monitor.ProfileTimeSettings", "模式「{0}」的时间段设置", selected.Name);
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
                MessageBox.Show(Loc.T("Monitor.KeepOneProfile", "至少需要保留一个情境模式。"), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Information);
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

        private void BtnRenameMode_Click(object sender, RoutedEventArgs e)
        {
            var selected = LstModes.SelectedItem as ModeItemViewModel;
            if (selected == null)
            {
                MessageBox.Show(Loc.T("Monitor.SelectProfileToRename", "请先选择一个要重命名的方法模式。"), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string oldName = selected.Name;
            if (PromptInput(this, Loc.T("Monitor.RenameTitle", "重命名情境模式"), Loc.T("Monitor.RenamePrompt", "请输入模式「{0}」的新名称：", oldName), oldName, out string newName))
            {
                newName = newName?.Trim();
                if (string.IsNullOrWhiteSpace(newName))
                {
                    MessageBox.Show(Loc.T("Monitor.NameEmpty", "模式名称不能为空。"), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (_modes.Any(m => string.Equals(m.Name, newName, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show(Loc.T("Monitor.NameExists", "已存在名为「{0}」的模式，请使用其他名称。", newName), Loc.T("Msg.NameDuplicate", "名称重复"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 迁移模式时间设置
                if (_modeSettings.TryGetValue(oldName, out var settings))
                {
                    _modeSettings.Remove(oldName);
                    _modeSettings[newName] = settings;
                }

                // 如果当前激活的是该模式，同步更新激活名称
                if (string.Equals(_activeProfileName, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    _activeProfileName = newName;
                }

                selected.Name = newName;
                TxtCurrentModeTitle.Text = Loc.T("Monitor.ProfileTimeSettings", "模式「{0}」的时间段设置", selected.Name);
            }
        }

        public static bool PromptInput(Window owner, string title, string prompt, string defaultText, out string result)
        {
            result = defaultText;
            var dlg = new Window
            {
                Title = title,
                Width = 360,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
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
                Text = prompt,
                Margin = new Thickness(0, 0, 0, 8),
                FontSize = 13,
                Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#FF374151")
            };
            var txt = new TextBox
            {
                Text = defaultText,
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

            btnOk.Click += (s, ev) => { dlg.DialogResult = true; dlg.Close(); };
            btnCancel.Click += (s, ev) => { dlg.DialogResult = false; dlg.Close(); };

            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);

            Grid.SetRow(lbl, 0);
            Grid.SetRow(txt, 1);
            Grid.SetRow(btnPanel, 2);

            grid.Children.Add(lbl);
            grid.Children.Add(txt);
            grid.Children.Add(btnPanel);

            dlg.Content = grid;
            dlg.Loaded += (s, ev) => txt.Focus();

            if (dlg.ShowDialog() == true)
            {
                result = txt.Text.Trim();
                return true;
            }
            return false;
        }

        private void BtnAddTimeSetting_Click(object sender, RoutedEventArgs e)
        {
            var selected = LstModes.SelectedItem as ModeItemViewModel;
            if (selected == null || !_modeSettings.ContainsKey(selected.Name)) return;

            string timeStr = TxtNewTime.Text.Trim();
            if (!TimeSpan.TryParse(timeStr, out _))
            {
                MessageBox.Show(Loc.T("Monitor.InvalidTime", "请输入正确的时间格式 (HH:mm)，例如 18:30"), Loc.T("Msg.FormatError", "格式错误"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int b, c;
            if (!int.TryParse(TxtNewBrightness.Text, out b) || b < 0 || b > 100)
            {
                MessageBox.Show(Loc.T("Monitor.BrightnessRange", "亮度值必须在 0 到 100 之间。"), Loc.T("Msg.FormatError", "格式错误"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtNewContrast.Text, out c) || c < 0 || c > 100)
            {
                MessageBox.Show(Loc.T("Monitor.ContrastRange", "对比度值必须在 0 到 100 之间。"), Loc.T("Msg.FormatError", "格式错误"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    MessageBox.Show(Loc.T("Monitor.KeepOneTimePoint", "每个模式至少需要保留一个时间点。"), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                list.Remove(setting);
            }
        }

        private void GridTimeSettings_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var setting = GridTimeSettings.SelectedItem as MonitorTimeSetting;
            if (setting != null)
            {
                TxtNewTime.Text = setting.Time;
                TxtNewBrightness.Text = setting.Brightness.ToString();
                TxtNewContrast.Text = setting.Contrast.ToString();
            }
        }

        private void GridTimeSettings_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var setting = GridTimeSettings.SelectedItem as MonitorTimeSetting;
            if (setting != null)
            {
                TxtNewTime.Text = setting.Time;
                TxtNewBrightness.Text = setting.Brightness.ToString();
                TxtNewContrast.Text = setting.Contrast.ToString();
                TxtNewBrightness.Focus();
                TxtNewBrightness.SelectAll();
            }
        }

        private void BtnUpdateTimeSetting_Click(object sender, RoutedEventArgs e)
        {
            var selectedMode = LstModes.SelectedItem as ModeItemViewModel;
            if (selectedMode == null || !_modeSettings.ContainsKey(selectedMode.Name)) return;

            var setting = GridTimeSettings.SelectedItem as MonitorTimeSetting;
            if (setting == null)
            {
                MessageBox.Show(Loc.T("Monitor.SelectTimeRow", "请先在列表中选中一个要更新的时间点行。"), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string timeStr = TxtNewTime.Text.Trim();
            if (!TimeSpan.TryParse(timeStr, out _))
            {
                MessageBox.Show(Loc.T("Monitor.InvalidTime", "请输入正确的时间格式 (HH:mm)，例如 18:30"), Loc.T("Msg.FormatError", "格式错误"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtNewBrightness.Text, out int b) || b < 0 || b > 100)
            {
                MessageBox.Show(Loc.T("Monitor.BrightnessRange", "亮度值必须在 0 到 100 之间。"), Loc.T("Msg.FormatError", "格式错误"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtNewContrast.Text, out int c) || c < 0 || c > 100)
            {
                MessageBox.Show(Loc.T("Monitor.ContrastRange", "对比度值必须在 0 到 100 之间。"), Loc.T("Msg.FormatError", "格式错误"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            setting.Time = timeStr;
            setting.Brightness = b;
            setting.Contrast = c;

            // 重新按时间排序
            var list = _modeSettings[selectedMode.Name];
            var sorted = list.OrderBy(t => t.ToTimeSpan()).ToList();
            list.Clear();
            foreach (var item in sorted)
            {
                list.Add(item);
            }
            GridTimeSettings.SelectedItem = setting;
        }

        private void BtnResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Loc.T("Monitor.RestoreConfirm", "确认恢复到默认显示器情境与时间表吗？"), Loc.T("Common.Confirm", "确认"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var def = MonitorProfileConfig.CreateDefault();
                if (!_module.SaveAndApplyConfig(def))
                {
                    MessageBox.Show(Loc.T("Config.SaveFailed"), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                LoadConfigData();
            }
        }

        private void BtnTestApply_Click(object sender, RoutedEventArgs e)
        {
            var cfg = BuildCurrentConfigFromUi();
            if (!_module.SaveAndApplyConfig(cfg))
            {
                MessageBox.Show(Loc.T("Config.SaveFailed"), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            _module.ScheduleEngine.ApplyCurrentSetting(force: true);
            MessageBox.Show(Loc.T("Monitor.TestApplied", "已应用测试：当前情境 [{0}]", cfg.ActiveProfile), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var cfg = BuildCurrentConfigFromUi();
            if (!_module.SaveAndApplyConfig(cfg))
            {
                MessageBox.Show(Loc.T("Config.SaveFailed"), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            Close();
        }

        private MonitorProfileConfig BuildCurrentConfigFromUi()
        {
            var cfg = new MonitorProfileConfig();

            cfg.Enabled = _module?.Config?.Enabled ?? true;
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
