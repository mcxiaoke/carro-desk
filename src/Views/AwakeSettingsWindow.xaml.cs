using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CarroDesk.Core;
using CarroDesk.Modules.Awake;
using CarroDesk.Modules.Awake.Models;

namespace CarroDesk.Views
{
    public partial class AwakeSettingsWindow : Window
    {
        private readonly AwakeModule _module;
        private List<string> _processes;

        public AwakeSettingsWindow(AwakeModule module)
        {
            _module = module ?? throw new ArgumentNullException(nameof(module));
            InitializeComponent();
            LoadSettings();
            UpdateStatusBadge();
        }

        private void LoadSettings()
        {
            var config = _module.Config ?? AwakeConfig.CreateDefault();
            var service = _module.Service;

            // 模式
            var currentMode = service != null ? service.Mode : config.Mode;
            switch (currentMode)
            {
                case AwakeMode.Passive:
                    RadioPassive.IsChecked = true;
                    break;
                case AwakeMode.Indefinite:
                    RadioIndefinite.IsChecked = true;
                    break;
                case AwakeMode.Timed:
                    RadioTimed.IsChecked = true;
                    if (service != null && service.RemainingTime > TimeSpan.Zero)
                    {
                        TimedMinutesBox.Text = Math.Max(1, (int)Math.Ceiling(service.RemainingTime.TotalMinutes)).ToString();
                    }
                    else
                    {
                        TimedMinutesBox.Text = config.DefaultDurationMinutes.ToString();
                    }
                    break;
                case AwakeMode.UntilTime:
                    RadioUntilTime.IsChecked = true;
                    if (service != null && service.ExpireTime > DateTime.Now)
                    {
                        UntilTimeBox.Text = service.ExpireTime.ToString("HH:mm");
                    }
                    break;
                default:
                    RadioPassive.IsChecked = true;
                    break;
            }

            KeepDisplayOnBox.IsChecked = service != null ? service.KeepDisplayOn : config.KeepDisplayOn;
            DisableOnBatteryBox.IsChecked = config.DisableOnBattery;
            BatteryThresholdBox.Text = config.BatteryThreshold.ToString();
            HotkeyBox.Text = config.Hotkey ?? "Win+Shift+W";

            _processes = config.AutoAwakeProcesses != null ? new List<string>(config.AutoAwakeProcesses) : new List<string>();
            ProcessListBox.ItemsSource = _processes;
        }

        private void UpdateStatusBadge()
        {
            var service = _module.Service;
            if (service == null) return;

            if (service.IsBatteryPaused)
            {
                StatusBadge.Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xF3, 0xC7));
                StatusBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E));
                StatusBadgeText.Text = "已暂停 (电池供电)";
            }
            else if (service.IsProcessTriggered)
            {
                StatusBadge.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xFD, 0xE7));
                StatusBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0x65, 0x34));
                StatusBadgeText.Text = $"进程唤醒中 ({service.ActiveProcessTrigger})";
            }
            else if (service.Mode == AwakeMode.Indefinite)
            {
                StatusBadge.Background = new SolidColorBrush(Color.FromRgb(0xDB, 0xEA, 0xFE));
                StatusBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x40, 0xAF));
                StatusBadgeText.Text = "无限期保持唤醒";
            }
            else if (service.Mode == AwakeMode.Timed || service.Mode == AwakeMode.UntilTime)
            {
                StatusBadge.Background = new SolidColorBrush(Color.FromRgb(0xDB, 0xEA, 0xFE));
                StatusBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x40, 0xAF));
                var rem = service.RemainingTime;
                StatusBadgeText.Text = $"倒计时中 (剩余 {(int)rem.TotalMinutes}分)";
            }
            else
            {
                StatusBadge.Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
                StatusBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
                StatusBadgeText.Text = "已关闭 (跟随系统)";
            }
        }

        private void OnPickRunningProcessClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var procs = Process.GetProcesses()
                    .Where(p =>
                    {
                        try { return !string.IsNullOrWhiteSpace(p.MainWindowTitle) && !string.IsNullOrWhiteSpace(p.ProcessName); }
                        catch { return false; }
                    })
                    .OrderBy(p => p.ProcessName)
                    .Select(p => p.ProcessName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (procs.Count == 0)
                {
                    MessageBox.Show("未找到具有窗口的运行中应用程序。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var menu = new ContextMenu();
                foreach (var p in procs)
                {
                    var item = new MenuItem { Header = p };
                    item.Click += (s, ev) => AddProcess(p);
                    menu.Items.Add(item);
                }

                var btn = sender as Button;
                if (btn != null)
                {
                    menu.PlacementTarget = btn;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.IsOpen = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("获取运行中进程失败：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnAddProcessClick(object sender, RoutedEventArgs e)
        {
            string txt = ProcessInputBox.Text.Trim();
            if (string.IsNullOrEmpty(txt)) return;

            var parts = txt.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                AddProcess(p.Trim());
            }
            ProcessInputBox.Text = string.Empty;
        }

        private void AddProcess(string procName)
        {
            if (string.IsNullOrWhiteSpace(procName)) return;
            string clean = Path.GetFileNameWithoutExtension(procName.Trim());
            if (!_processes.Any(p => string.Equals(p, clean, StringComparison.OrdinalIgnoreCase)))
            {
                _processes.Add(clean);
                ProcessListBox.ItemsSource = null;
                ProcessListBox.ItemsSource = _processes;
            }
        }

        private void OnDeleteProcessClick(object sender, RoutedEventArgs e)
        {
            var sel = ProcessListBox.SelectedItem as string;
            if (sel == null) return;

            _processes.Remove(sel);
            ProcessListBox.ItemsSource = null;
            ProcessListBox.ItemsSource = _processes;
        }

        private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确定要将设置恢复为默认配置吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var def = AwakeConfig.CreateDefault();
            RadioPassive.IsChecked = true;
            TimedMinutesBox.Text = def.DefaultDurationMinutes.ToString();
            UntilTimeBox.Text = "18:00";
            KeepDisplayOnBox.IsChecked = def.KeepDisplayOn;
            DisableOnBatteryBox.IsChecked = def.DisableOnBattery;
            BatteryThresholdBox.Text = def.BatteryThreshold.ToString();
            HotkeyBox.Text = def.Hotkey;
            _processes = new List<string>();
            ProcessListBox.ItemsSource = null;
            ProcessListBox.ItemsSource = _processes;
        }

        private void OnSaveApplyClick(object sender, RoutedEventArgs e)
        {
            int defaultMins = 30;
            if (int.TryParse(TimedMinutesBox.Text.Trim(), out int parsedMins) && parsedMins > 0)
            {
                defaultMins = parsedMins;
            }

            int threshold = 20;
            if (int.TryParse(BatteryThresholdBox.Text.Trim(), out int parsedThreshold))
            {
                threshold = Math.Max(5, Math.Min(95, parsedThreshold));
            }

            var config = _module.Config ?? new AwakeConfig();
            config.KeepDisplayOn = KeepDisplayOnBox.IsChecked == true;
            config.DisableOnBattery = DisableOnBatteryBox.IsChecked == true;
            config.BatteryThreshold = threshold;
            config.DefaultDurationMinutes = defaultMins;
            config.Hotkey = HotkeyBox.Text.Trim();
            config.AutoAwakeProcesses = new List<string>(_processes);

            // 应用工作模式
            var service = _module.Service;
            if (service != null)
            {
                service.UpdateConfig(config);

                if (RadioPassive.IsChecked == true)
                {
                    config.Mode = AwakeMode.Passive;
                    service.SetPassive();
                }
                else if (RadioIndefinite.IsChecked == true)
                {
                    config.Mode = AwakeMode.Indefinite;
                    service.SetIndefinite();
                }
                else if (RadioTimed.IsChecked == true)
                {
                    config.Mode = AwakeMode.Timed;
                    service.SetTimed(defaultMins);
                }
                else if (RadioUntilTime.IsChecked == true)
                {
                    config.Mode = AwakeMode.UntilTime;
                    if (TimeSpan.TryParse(UntilTimeBox.Text.Trim(), out TimeSpan targetTimeSpan))
                    {
                        DateTime targetDate = DateTime.Today.Add(targetTimeSpan);
                        service.SetUntilTime(targetDate);
                    }
                    else
                    {
                        MessageBox.Show("指定时刻格式无效，请输入有效时间如 18:00。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
            }

            _module.SaveConfig();
            _module.RequestRefreshTray();
            Close();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
