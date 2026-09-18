using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CarroDesk.Core;
using CarroDesk.Host.Services;
using CarroDesk.Modules.AppAutoMute.Models;
using CarroDesk;
using CarroDesk.Models;
using CarroDesk.Services;
using CarroDesk.Services.Tasks;

namespace CarroDesk.Views
{
    public partial class AppAutoMuteSettingsWindow : Window
    {
        private readonly ObservableCollection<string> _targetApps = new ObservableCollection<string>();

        public AppAutoMuteSettingsWindow()
        {
            InitializeComponent();
            LstTargetApps.ItemsSource = _targetApps;
            LoadCurrentSettings();
            LoadRunningApps();
        }

        private void LoadCurrentSettings()
        {
            var config = App.AppAutoMuteMod?.Config ?? new AppAutoMuteConfig();

            ChkEnabled.IsChecked = config.Enabled;
            TxtHotkey.Text = config.Hotkey ?? "Ctrl+Win+S";

            SldMuteDelay.Value = Math.Max(100, Math.Min(5000, config.MuteDelayMs));
            TxtMuteDelayVal.Text = $"{SldMuteDelay.Value} ms";

            SldUnmuteDelay.Value = Math.Max(50, Math.Min(3000, config.UnmuteDelayMs));
            TxtUnmuteDelayVal.Text = $"{SldUnmuteDelay.Value} ms";

            if (string.Equals(config.Mode, "Whitelist", StringComparison.OrdinalIgnoreCase))
            {
                RadWhitelist.IsChecked = true;
            }
            else
            {
                RadBlacklist.IsChecked = true;
            }

            _targetApps.Clear();
            if (config.TargetApps != null)
            {
                foreach (var app in ProcessHelper.NormalizeList(config.TargetApps))
                {
                    _targetApps.Add(app);
                }
            }
        }

        private void LoadRunningApps()
        {
            var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // 1. 优先获取当前正在发生音频输出的进程
                var audioService = App.Services?.GetService<AudioService>();
                if (audioService != null)
                {
                    var audioProcs = audioService.GetActiveAudioProcesses();
                    foreach (var p in audioProcs)
                    {
                        string norm = ProcessHelper.Normalize(p);
                        if (!string.IsNullOrEmpty(norm)) detected.Add(norm);
                    }
                }
            }
            catch { }

            try
            {
                // 2. 辅以系统带有主窗口的 GUI 进程
                var winApps = ProcessHelper.GetRunningWindowProcesses();
                foreach (var app in winApps)
                {
                    detected.Add(app.ProcessName);
                }
            }
            catch { }

            var sorted = detected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            CboRunningApps.ItemsSource = sorted;
            if (sorted.Count > 0)
            {
                CboRunningApps.SelectedIndex = 0;
            }
        }

        private void OnMuteDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtMuteDelayVal != null)
            {
                int val = (int)Math.Round(e.NewValue / 50.0) * 50;
                TxtMuteDelayVal.Text = $"{val} ms";
            }
        }

        private void OnUnmuteDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtUnmuteDelayVal != null)
            {
                int val = (int)Math.Round(e.NewValue / 50.0) * 50;
                TxtUnmuteDelayVal.Text = $"{val} ms";
            }
        }

        private void OnStatusChanged(object sender, RoutedEventArgs e)
        {
            // 可根据开关调整部分输入框激活状态
        }

        private void OnAddRunningAppClick(object sender, RoutedEventArgs e)
        {
            if (CboRunningApps.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
            {
                AddAppToList(selected);
            }
        }

        private void OnAddCustomAppClick(object sender, RoutedEventArgs e)
        {
            AddCustomApp();
        }

        private void OnCustomAppKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddCustomApp();
            }
        }

        private void AddCustomApp()
        {
            string raw = TxtCustomApp.Text?.Trim();
            if (string.IsNullOrWhiteSpace(raw)) return;

            var parts = raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                string norm = ProcessHelper.Normalize(part);
                if (!string.IsNullOrEmpty(norm))
                {
                    AddAppToList(norm);
                }
            }
            TxtCustomApp.Clear();
        }

        private void AddAppToList(string app)
        {
            string norm = ProcessHelper.Normalize(app);
            if (string.IsNullOrEmpty(norm)) return;

            if (!_targetApps.Any(x => ProcessHelper.IsMatch(x, norm)))
            {
                _targetApps.Add(norm);
            }
        }

        private void OnRemoveAppClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string app)
            {
                _targetApps.Remove(app);
            }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            string hotkey = TxtHotkey.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(hotkey))
            {
                if (!HotkeyHelper.Validate(hotkey, out string err))
                {
                    MessageBox.Show($"快捷键格式不正确: {err}\n支持格式例如: Ctrl+Win+S, Ctrl+Alt+M", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var configMgr = App.Services?.GetService<IConfigManager>();
            var config = App.AppAutoMuteMod?.Config ?? new AppAutoMuteConfig();

            config.Enabled = ChkEnabled.IsChecked == true;
            config.Hotkey = hotkey;
            config.MuteDelayMs = (int)SldMuteDelay.Value;
            config.UnmuteDelayMs = (int)SldUnmuteDelay.Value;
            config.Mode = RadWhitelist.IsChecked == true ? "Whitelist" : "Blacklist";
            config.TargetApps = _targetApps.ToList();

            if (configMgr != null)
            {
                configMgr.SaveModuleConfig("AppAutoMute", config);
            }

            App.AppAutoMuteMod?.OnConfigReloaded();
            App.ShowBalloonPublic("后台智能静音配置已保存并生效");

            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
