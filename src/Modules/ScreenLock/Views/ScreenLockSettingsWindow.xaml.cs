using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CarroDesk.Core;
using CarroDesk.Host.Services;
using CarroDesk.Modules.ScreenLock.Models;
using CarroDesk.Services;
using CarroDesk.Services.Localization;
using CarroDesk.Services.Tasks;

namespace CarroDesk.Modules.ScreenLock.Views
{
    /// <summary>
    /// 屏幕锁定与闲时保护设置窗口（P2-16：从宿主 ConfigEditorWindow 拆分到模块自有命名空间）。
    /// 配置读写统一经 IConfigManager.GetModuleConfig / SaveModuleConfig，宿主不再 import 模块 Model。
    /// </summary>
    public partial class ScreenLockSettingsWindow : Window
    {
        private ScreenLockConfig _editing;
        private readonly IConfigManager _configManager;
        private readonly Action _onApplied;

        public ScreenLockSettingsWindow(IConfigManager configManager = null, Action onApplied = null)
        {
            InitializeComponent();
            _configManager = configManager;
            _onApplied = onApplied;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => LoadCurrent();

        private void LoadCurrent()
        {
            try
            {
                _editing = _configManager?.GetModuleConfig<ScreenLockConfig>("ScreenLock")?.Clone() ?? new ScreenLockConfig();
                ApplyEditingToUi();
                ValidateText.Text = "";
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Config.LoadFailed", ex.Message), Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyEditingToUi()
        {
            string idleStr = _editing.IdleMinutes.ToString();
            bool found = false;
            for (int i = 0; i < IdleBox.Items.Count; i++)
            {
                var item = IdleBox.Items[i] as ComboBoxItem;
                if (item != null)
                {
                    string txt = item.Content.ToString();
                    string num = txt.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    if (num == idleStr) { IdleBox.SelectedIndex = i; found = true; break; }
                }
            }
            if (!found) { IdleBox.Text = idleStr; IdleBox.SelectedIndex = -1; }
            else IdleBox.Text = idleStr;

            ShowClockBox.IsChecked = _editing.ShowClock;
            OpacitySlider.Value = _editing.OverlayOpacity;
            UpdateOpacityText(_editing.OverlayOpacity);
            UnlockOnResumeBox.IsChecked = _editing.UnlockOnResume;
            HotkeyBox.Text = string.IsNullOrWhiteSpace(_editing.Hotkey) ? "Ctrl+Alt+L" : _editing.Hotkey;

            ExcludeList.ItemsSource = null;
            var list = _editing.ExcludeProcesses != null ? ProcessHelper.NormalizeList(_editing.ExcludeProcesses) : new List<string>();
            ExcludeList.ItemsSource = list;
        }

        private void ExcludeInputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) { OnExcludeAddClick(sender, null); e.Handled = true; }
        }

        private void OnPickRunningProcessClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var btn = sender as Button;
                var runningApps = ProcessHelper.GetRunningWindowProcesses();
                var menu = new ContextMenu();

                foreach (var app in runningApps)
                {
                    var item = new MenuItem { Header = app.DisplayText, Tag = app.ProcessName };
                    item.Click += (s, ev) => AddExcludeProcess(item.Tag.ToString());
                    menu.Items.Add(item);
                }

                if (menu.Items.Count == 0)
                {
                    menu.Items.Add(new MenuItem { Header = Loc.T("Tray.NoRecentTasks"), IsEnabled = false });
                }

                if (btn != null)
                {
                    menu.PlacementTarget = btn;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.IsOpen = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Config.GetProcessesFailed", ex.Message), Loc.T("Common.Prompt"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void AddExcludeProcess(string processName)
        {
            string norm = ProcessHelper.Normalize(processName);
            if (string.IsNullOrEmpty(norm)) return;

            var list = (ExcludeList.ItemsSource as List<string>) ?? new List<string>();
            if (!list.Any(x => ProcessHelper.IsMatch(x, norm)))
            {
                list.Add(norm);
                ExcludeList.ItemsSource = null;
                ExcludeList.ItemsSource = list;
            }
        }

        private void OnExcludeAddClick(object sender, RoutedEventArgs e)
        {
            string v = ExcludeInputBox.Text.Trim();
            if (string.IsNullOrEmpty(v)) return;
            var parts = v.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                AddExcludeProcess(p.Trim());
            }
            ExcludeInputBox.Text = "";
        }

        private void OnBrowseExcludeExeClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = Loc.T("Common.ExeFilter", "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"),
                    Title = Loc.T("Msg.SelectExcludeExe", "选择排除进程应用程序 (.exe)")
                };
                if (ofd.ShowDialog(this) == true)
                {
                    string fileName = System.IO.Path.GetFileNameWithoutExtension(ofd.FileName);
                    ExcludeInputBox.Text = fileName;
                    AddExcludeProcess(fileName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Loc.T("Msg.OpenFileFailed", "选择文件失败: {0}", ex.Message), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnExcludeDeleteClick(object sender, RoutedEventArgs e)
        {
            var sel = ExcludeList.SelectedItem as string;
            if (sel == null) return;
            var list = (ExcludeList.ItemsSource as List<string>) ?? new List<string>();
            list.Remove(sel);
            ExcludeList.ItemsSource = null;
            ExcludeList.ItemsSource = list;
        }

        private void OnExcludeClearClick(object sender, RoutedEventArgs e)
        {
            var list = (ExcludeList.ItemsSource as List<string>) ?? new List<string>();
            if (list.Count == 0) return;
            if (MessageBox.Show(this, Loc.T("Msg.ClearExcludeList", "确定要清空排除进程列表吗？"), Loc.T("Msg.ConfirmClear", "确认清空"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                list.Clear();
                ExcludeList.ItemsSource = null;
                ExcludeList.ItemsSource = list;
            }
        }

        private void ExcludeList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            OnExcludeDeleteClick(sender, null);
        }

        private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Loc.T("Config.ResetDefaultsConfirm"), Loc.T("Config.ResetDefaultsTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var def = new ScreenLockConfig();
            _editing.IdleMinutes = def.IdleMinutes;
            _editing.ShowClock = def.ShowClock;
            _editing.OverlayOpacity = def.OverlayOpacity;
            _editing.Hotkey = def.Hotkey;
            _editing.UnlockOnResume = def.UnlockOnResume;
            _editing.ExcludeProcesses = new List<string>();
            ApplyEditingToUi();
            ValidateText.Text = Loc.T("Config.ResetDefaultsTooltip");
        }

        private void UpdateOpacityText(double opacity)
        {
            if (OpacityText != null)
            {
                int pct = (int)Math.Round(opacity * 100);
                OpacityText.Text = string.Format("{0}%", pct);
            }
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateOpacityText(e.NewValue);
        }

        private void SyncFromUI()
        {
            if (_editing == null) _editing = new ScreenLockConfig();

            string idleRaw = IdleBox.Text.Trim();
            if (idleRaw.Contains("-")) idleRaw = idleRaw.Split('-')[0].Trim();
            if (!int.TryParse(idleRaw, out int idle)) idle = _editing.IdleMinutes;
            _editing.IdleMinutes = idle;
            _editing.ShowClock = ShowClockBox.IsChecked == true;
            _editing.OverlayOpacity = Math.Round(OpacitySlider.Value, 2);
            _editing.Hotkey = HotkeyBox.Text.Trim();
            _editing.UnlockOnResume = UnlockOnResumeBox.IsChecked == true;
            var excl = ExcludeList.ItemsSource as List<string>;
            _editing.ExcludeProcesses = ProcessHelper.NormalizeList(excl);
        }

        private string Validate()
        {
            if (_editing.IdleMinutes < 0 || _editing.IdleMinutes > 24 * 60) return Loc.T("Config.ErrorIdleRange");
            if (_editing.OverlayOpacity < 0.3 || _editing.OverlayOpacity > 1.0) return Loc.T("Config.OpacityRange", "透明度需在 0.3-1.0 之间");
            if (!string.IsNullOrWhiteSpace(_editing.Hotkey))
            {
                if (!HotkeyHelper.Validate(_editing.Hotkey, out string hkErr))
                {
                    return Loc.T("Config.HotkeyInvalid", "全局锁屏热键格式无效: {0}", hkErr);
                }
            }
            foreach (var p in _editing.ExcludeProcesses)
            {
                if (p.Length > 260) return Loc.T("Config.ExcludeNameTooLong", "排除进程名过长: {0}", p);
                if (p.IndexOfAny(new[] { '<', '>', ':', '\"', '|', '?', '*' }) >= 0) return Loc.T("Config.ExcludeNameInvalidChars", "排除进程名含非法字符: {0}", p);
            }
            return null;
        }

        private void OnSaveApplyClick(object sender, RoutedEventArgs e)
        {
            SyncFromUI();
            string err = Validate();
            if (err != null)
            {
                ValidateText.Text = Loc.T("Config.ValidationFailed") + ": " + err;
                MessageBox.Show(err, Loc.T("Config.ValidationFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                if (_configManager == null || !_configManager.SaveModuleConfig("ScreenLock", _editing))
                    throw new InvalidOperationException(Loc.T("Config.SaveFailed"));
                ValidateText.Text = Loc.T("Common.Success");
                // 持久化后通知模块重载应用，不越权直驱模块/Controller（规范 M9）
                _onApplied?.Invoke();
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Config.SaveFailed", ex.Message), Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) { Close(); }
    }
}