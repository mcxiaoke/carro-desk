using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CarroDesk.Core;
using CarroDesk.Host.Services;
using CarroDesk.Models;
using CarroDesk.Modules.ScreenLock.Models;
using CarroDesk.Modules.TaskScheduler.Models;
using CarroDesk.Services;
using CarroDesk.Services.Localization;
using CarroDesk.Services.Tasks;

namespace CarroDesk.Views
{
    public partial class ConfigEditorWindow : Window
    {
        private AppSettings _editing;
        private ScreenLockConfig _editingScreenLock;
        private TaskSchedulerConfig _editingTaskScheduler;
        private bool _isInitializing = false;
        private readonly IPinService _pinService;
        private readonly ConfigManager _configManager;
        private readonly Action _onApplied;

        public ConfigEditorWindow(IPinService pinService, ConfigManager configManager = null, Action onApplied = null)
        {
            InitializeComponent();
            _pinService = pinService;
            _configManager = configManager;
            _onApplied = onApplied;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadCurrent();
        }

        private void LoadCurrent()
        {
            try
            {
                _isInitializing = true;
                var cur = _configManager?.Current ?? new AppSettings();
                _editing = cur.Clone();

                _editingScreenLock = _configManager?.GetModuleConfig<ScreenLockConfig>("ScreenLock")?.Clone() ?? new ScreenLockConfig();
                _editingTaskScheduler = _configManager?.GetModuleConfig<TaskSchedulerConfig>("TaskScheduler")?.Clone() ?? new TaskSchedulerConfig();

                PathText.Text = ConfigService.FilePath;
                RefreshDynamicTexts();

                // Language
                string curLang = _editing.Language ?? "auto";
                for (int i = 0; i < LanguageBox.Items.Count; i++)
                {
                    if (LanguageBox.Items[i] is ComboBoxItem item && item.Tag != null)
                    {
                        if (string.Equals(item.Tag.ToString(), curLang, StringComparison.OrdinalIgnoreCase))
                        {
                            LanguageBox.SelectedIndex = i;
                            break;
                        }
                    }
                }

                // IdleMinutes -> ComboBox (editable)
                string idleStr = _editingScreenLock.IdleMinutes.ToString();
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

                ShowClockBox.IsChecked = _editingScreenLock.ShowClock;
                OpacitySlider.Value = _editingScreenLock.OverlayOpacity;
                UpdateOpacityText(_editingScreenLock.OverlayOpacity);

                AutoStartBox.IsChecked = _editing.AutoStart;
                UnlockOnResumeBox.IsChecked = _editingScreenLock.UnlockOnResume;
                TasksEnabledBox.IsChecked = _editingTaskScheduler.GlobalEnabled;
                HotkeyBox.Text = string.IsNullOrWhiteSpace(_editingScreenLock.Hotkey) ? "Ctrl+Alt+L" : _editingScreenLock.Hotkey;

                ExcludeList.ItemsSource = null;
                var list = _editingScreenLock.ExcludeProcesses != null ? ProcessHelper.NormalizeList(_editingScreenLock.ExcludeProcesses) : new List<string>();
                ExcludeList.ItemsSource = list;

                ValidateText.Text = "";
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Config.LoadFailed", ex.Message), Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isInitializing = false;
            }
        }

        private void RefreshDynamicTexts()
        {
            ModeText.Text = ConfigService.IsPortableMode ? Loc.T("Config.PortableMode") : Loc.T("Config.StandardMode");
            if (_editing != null)
            {
                PinStatusText.Text = _editing.HasPin() ? Loc.T("Config.PinConfigured") + " (" + MaskHash(_editing.PinHash) + ")" : Loc.T("Config.PinNotConfigured");
            }
        }

        private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (LanguageBox.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                string lang = item.Tag.ToString();
                _editing.Language = lang;
                I18nService.Instance.SetLanguage(lang);
                RefreshDynamicTexts();
                UpdateOpacityText(OpacitySlider.Value);
            }
        }

        private string MaskHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return "";
            if (hash.Length <= 8) return "***";
            return hash.Substring(0, 6) + "***";
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
                    item.Click += (s, ev) =>
                    {
                        AddExcludeProcess(item.Tag.ToString());
                    };
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
            // allow comma or semicolon separated
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

        private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Loc.T("Config.ResetDefaultsConfirm"), Loc.T("Config.ResetDefaultsTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var defHost = new AppSettings();
            var defScreenLock = new ScreenLockConfig();
            var defTaskScheduler = new TaskSchedulerConfig();

            IdleBox.Text = defScreenLock.IdleMinutes.ToString();
            ShowClockBox.IsChecked = defScreenLock.ShowClock;
            OpacitySlider.Value = defScreenLock.OverlayOpacity;
            UpdateOpacityText(defScreenLock.OverlayOpacity);
            HotkeyBox.Text = defScreenLock.Hotkey ?? "Ctrl+Alt+L";
            AutoStartBox.IsChecked = defHost.AutoStart;
            UnlockOnResumeBox.IsChecked = defScreenLock.UnlockOnResume;
            TasksEnabledBox.IsChecked = defTaskScheduler.GlobalEnabled;
            ExcludeList.ItemsSource = null;
            ExcludeList.ItemsSource = new List<string>();
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

        private void OnOpenConfigDirClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = ConfigService.DirPath;
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(dir);
            }
            catch { }
        }

        private void ExcludeList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            OnExcludeDeleteClick(sender, null);
        }

        private void SyncFromUI()
        {
            if (_editing == null) _editing = new AppSettings();
            if (_editingScreenLock == null) _editingScreenLock = new ScreenLockConfig();
            if (_editingTaskScheduler == null) _editingTaskScheduler = new TaskSchedulerConfig();

            _editing.AutoStart = AutoStartBox.IsChecked == true;
            _editing.Language = _editing.Language ?? "auto";

            string idleRaw = IdleBox.Text.Trim();
            if (idleRaw.Contains("-")) idleRaw = idleRaw.Split('-')[0].Trim();
            if (!int.TryParse(idleRaw, out int idle)) idle = _editingScreenLock.IdleMinutes;
            _editingScreenLock.IdleMinutes = idle;
            _editingScreenLock.ShowClock = ShowClockBox.IsChecked == true;
            _editingScreenLock.OverlayOpacity = Math.Round(OpacitySlider.Value, 2);
            _editingScreenLock.Hotkey = HotkeyBox.Text.Trim();
            _editingScreenLock.UnlockOnResume = UnlockOnResumeBox.IsChecked == true;
            var excl = ExcludeList.ItemsSource as List<string>;
            _editingScreenLock.ExcludeProcesses = ProcessHelper.NormalizeList(excl);

            _editingTaskScheduler.GlobalEnabled = TasksEnabledBox.IsChecked == true;
        }

        private string Validate()
        {
            if (_editingScreenLock.IdleMinutes < 0 || _editingScreenLock.IdleMinutes > 24 * 60) return Loc.T("Config.ErrorIdleRange");
            if (_editingScreenLock.OverlayOpacity < 0.3 || _editingScreenLock.OverlayOpacity > 1.0) return Loc.T("Config.OpacityRange", "透明度需在 0.3-1.0 之间");
            if (!string.IsNullOrWhiteSpace(_editingScreenLock.Hotkey))
            {
                if (!HotkeyHelper.Validate(_editingScreenLock.Hotkey, out string hkErr))
                {
                    return Loc.T("Config.HotkeyInvalid", "全局锁屏热键格式无效: {0}", hkErr);
                }
            }
            foreach (var p in _editingScreenLock.ExcludeProcesses)
            {
                if (p.Length > 260) return Loc.T("Config.ExcludeNameTooLong", "排除进程名过长: {0}", p);
                if (p.IndexOfAny(new[] { '<', '>', ':', '\"', '|', '?', '*' }) >= 0) return Loc.T("Config.ExcludeNameInvalidChars", "排除进程名含非法字符: {0}", p);
            }
            return null;
        }

        private void OnValidateClick(object sender, RoutedEventArgs e)
        {
            SyncFromUI();
            string err = Validate();
            if (err != null)
            {
                ValidateText.Text = Loc.T("Config.ValidationFailed") + ": " + err;
                MessageBox.Show(err, Loc.T("Config.ValidationFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                ValidateText.Text = Loc.T("Config.ValidationPassed");
                MessageBox.Show(Loc.T("Config.ValidationPassed"), Loc.T("Common.Prompt"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (!DoSave(false)) return;
            MessageBox.Show(Loc.T("Config.SaveSuccess", ConfigService.FilePath), Loc.T("Common.Success"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnSaveApplyClick(object sender, RoutedEventArgs e)
        {
            if (!DoSave(true)) return;
            MessageBox.Show(Loc.T("Config.SaveSuccessApplied"), Loc.T("Common.Success"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private bool DoSave(bool apply)
        {
            SyncFromUI();
            string err = Validate();
            if (err != null)
            {
                ValidateText.Text = Loc.T("Config.ValidationFailed") + ": " + err;
                MessageBox.Show(err, Loc.T("Config.ValidationFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            try
            {
                if (_configManager != null)
                {
                    var target = _configManager.Current;
                    if (target != null)
                    {
                        _editing.CopyTo(target);
                        _editing = target.Clone();
                    }
                    _configManager.SaveModuleConfig("ScreenLock", _editingScreenLock);
                    _configManager.SaveModuleConfig("TaskScheduler", _editingTaskScheduler);
                    _configManager.Save();
                }
                ValidateText.Text = Loc.T("Common.Success");

                if (apply)
                {
                    ApplyRuntime();
                }
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Config.SaveFailed", ex.Message), Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void ApplyRuntime()
        {
            // 规范 M9：Config 编辑器不越权直驱模块/Controller。
            // 仅在持久化后向 Host 发一次重载通知，由各模块 OnConfigReloaded 自行应用。
            try
            {
                _onApplied?.Invoke();
            }
            catch { }
        }

        private void OnPinChangeClick(object sender, RoutedEventArgs e)
        {
            // verify old pin first if exists
            if (_editing.HasPin())
            {
                var verify = new VerifyPinWindow(_pinService, Loc.T("Config.VerifyOldPinPrompt"));
                verify.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                verify.Owner = this;
                if (verify.ShowDialog() != true)
                    return;
            }
            var first = new FirstRunWindow { IsChangeMode = true };
            first.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            first.Owner = this;
            if (first.ShowDialog() == true)
            {
                string newPin = first.NewPin;
                // generate new salt/hash without mutating controller yet (apply on Save)
                byte[] salt = PinService.GenerateSalt();
                string saltStr = Convert.ToBase64String(salt);
                string hashStr = PinService.ComputeHash(salt, newPin);
                _editing.PinSalt = saltStr;
                _editing.PinHash = hashStr;
                PinStatusText.Text = Loc.T("Config.PinConfigured") + " (" + MaskHash(_editing.PinHash) + ") *" + Loc.T("Config.PinModifiedHint");
                ValidateText.Text = Loc.T("Config.PinModifiedHint");
                MessageBox.Show(Loc.T("Config.NewPinGenerated"), Loc.T("Common.Prompt"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) { Close(); }
    }
}
