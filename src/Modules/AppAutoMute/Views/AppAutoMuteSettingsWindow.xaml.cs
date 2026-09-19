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
using CarroDesk.Modules.AppAutoMute;
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
        private readonly AppAutoMuteModule _module;
        private readonly IConfigManager _configManager;
        private readonly IAudioService _audioService;
        private readonly Action<string> _notifier;

        public AppAutoMuteSettingsWindow(
            AppAutoMuteModule module = null,
            IConfigManager configManager = null,
            IAudioService audioService = null,
            Action<string> notifier = null)
        {
            _module = module;
            _configManager = configManager;
            _audioService = audioService;
            _notifier = notifier;

            InitializeComponent();
            LstTargetApps.ItemsSource = _targetApps;
            LoadCurrentSettings();
            LoadRunningApps();
        }

        private void LoadCurrentSettings()
        {
            var config = _module?.Config ?? (_configManager != null ? _configManager.GetModuleConfig<AppAutoMuteConfig>("AppAutoMute") : new AppAutoMuteConfig());

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
                var audioService = _audioService;
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

        private void OnBrowseExeClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                    Title = "选择应用程序 (.exe)"
                };
                if (ofd.ShowDialog(this) == true)
                {
                    string fileName = System.IO.Path.GetFileNameWithoutExtension(ofd.FileName);
                    TxtCustomApp.Text = fileName;
                    AddAppToList(fileName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "选择文件失败: " + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnClearAppsClick(object sender, RoutedEventArgs e)
        {
            if (_targetApps.Count == 0) return;
            var res = MessageBox.Show(this, "确定要清空受控程序列表吗？", "确认清空", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                _targetApps.Clear();
            }
        }

        private void OnUnmuteAllClick(object sender, RoutedEventArgs e)
        {
            try
            {
                _module?.UnmuteAllTargets();
                if (_audioService != null)
                {
                    if (_targetApps.Count > 0)
                    {
                        _audioService.UnmuteProcesses(_targetApps);
                    }
                    var activeProcs = _audioService.GetActiveAudioProcesses();
                    if (activeProcs != null && activeProcs.Count > 0)
                    {
                        _audioService.UnmuteProcesses(activeProcs);
                    }
                }
                TxtNotice.Text = "已立即恢复所有声音";
                _notifier?.Invoke("已恢复所有应用程序声音");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "恢复声音失败: " + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            var configMgr = _configManager;
            var config = _module?.Config ?? (configMgr != null ? configMgr.GetModuleConfig<AppAutoMuteConfig>("AppAutoMute") : new AppAutoMuteConfig());

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

            _module?.OnConfigReloaded();
            string saveMsg = "后台智能静音配置已保存并生效";
            _notifier?.Invoke(saveMsg);

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
