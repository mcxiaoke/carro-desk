using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CarroDesk.Models;
using CarroDesk.Services;
using CarroDesk.Services.Localization;

namespace CarroDesk.Views
{
    public partial class ConfigEditorWindow : Window
    {
        private AppSettings _editing;
        private bool _isInitializing = false;

        public ConfigEditorWindow()
        {
            InitializeComponent();
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
                var cur = App.Config.Current;
                _editing = cur.Clone();

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

                AutoStartBox.IsChecked = _editing.AutoStart;
                UnlockOnResumeBox.IsChecked = _editing.UnlockOnResume;
                TasksEnabledBox.IsChecked = _editing.TasksEnabled;

                ExcludeList.ItemsSource = null;
                var list = _editing.ExcludeProcesses != null ? new List<string>(_editing.ExcludeProcesses) : new List<string>();
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
                var procs = System.Diagnostics.Process.GetProcesses();
                var menu = new ContextMenu();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 优先列出有主窗口的应用（日常前台应用与游戏）
                var windowApps = procs.Where(p =>
                {
                    try { return p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle); }
                    catch { return false; }
                }).OrderBy(p => p.ProcessName).ToList();

                foreach (var p in windowApps)
                {
                    try
                    {
                        string name = p.ProcessName + ".exe";
                        if (seen.Contains(name)) continue;
                        seen.Add(name);

                        string title = p.MainWindowTitle;
                        if (title.Length > 25) title = title.Substring(0, 22) + "...";
                        var item = new MenuItem { Header = string.Format("{0} ({1})", name, title), Tag = name };
                        item.Click += (s, ev) =>
                        {
                            AddExcludeProcess(item.Tag.ToString());
                        };
                        menu.Items.Add(item);
                    }
                    catch { }
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
            if (string.IsNullOrWhiteSpace(processName)) return;
            var list = (ExcludeList.ItemsSource as List<string>) ?? new List<string>();
            if (!list.Any(x => string.Equals(x, processName, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(processName);
                ExcludeList.ItemsSource = null;
                ExcludeList.ItemsSource = list;
            }
        }

        private void OnExcludeAddClick(object sender, RoutedEventArgs e)
        {
            string v = ExcludeInputBox.Text.Trim();
            if (string.IsNullOrEmpty(v)) return;
            // allow comma separated
            var parts = v.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                AddExcludeProcess(p.Trim());
            }
            ExcludeInputBox.Text = "";
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

        private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Loc.T("Config.ResetDefaultsConfirm"), Loc.T("Config.ResetDefaultsTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var def = new AppSettings();
            IdleBox.Text = def.IdleMinutes.ToString();
            ShowClockBox.IsChecked = def.ShowClock;
            OpacitySlider.Value = def.OverlayOpacity;
            UpdateOpacityText(def.OverlayOpacity);
            AutoStartBox.IsChecked = def.AutoStart;
            UnlockOnResumeBox.IsChecked = def.UnlockOnResume;
            TasksEnabledBox.IsChecked = def.TasksEnabled;
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

        private AppSettings BuildCurrent()
        {
            var s = new AppSettings();
            string idleRaw = IdleBox.Text.Trim();
            if (idleRaw.Contains("-")) idleRaw = idleRaw.Split('-')[0].Trim();
            int idle;
            if (!int.TryParse(idleRaw, out idle)) idle = _editing.IdleMinutes;
            s.IdleMinutes = idle;
            s.ShowClock = ShowClockBox.IsChecked == true;
            s.OverlayOpacity = Math.Round(OpacitySlider.Value, 2);
            s.AutoStart = AutoStartBox.IsChecked == true;
            s.UnlockOnResume = UnlockOnResumeBox.IsChecked == true;
            s.TasksEnabled = TasksEnabledBox.IsChecked == true;
            s.Language = _editing.Language ?? "auto";
            var excl = ExcludeList.ItemsSource as List<string>;
            s.ExcludeProcesses = excl != null ? new List<string>(excl) : new List<string>();
            // keep pin
            s.PinSalt = _editing.PinSalt;
            s.PinHash = _editing.PinHash;
            return s;
        }

        private string Validate(AppSettings s)
        {
            if (s.IdleMinutes < 0 || s.IdleMinutes > 24 * 60) return Loc.T("Config.ErrorIdleRange");
            if (s.OverlayOpacity < 0.3 || s.OverlayOpacity > 1.0) return "透明度需在 0.3-1.0 之间";
            foreach (var p in s.ExcludeProcesses)
            {
                if (p.Length > 260) return "排除进程名过长: " + p;
                if (p.IndexOfAny(new[] { '<', '>', ':', '\"', '|', '?', '*' }) >= 0) return "排除进程名含非法字符: " + p;
            }
            return null;
        }

        private void OnValidateClick(object sender, RoutedEventArgs e)
        {
            var cur = BuildCurrent();
            string err = Validate(cur);
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
            var cur = BuildCurrent();
            string err = Validate(cur);
            if (err != null)
            {
                ValidateText.Text = Loc.T("Config.ValidationFailed") + ": " + err;
                MessageBox.Show(err, Loc.T("Config.ValidationFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            try
            {
                // apply to global config
                cur.CopyTo(App.Config.Current);
                // also update editing copy (for pin)
                _editing = App.Config.Current.Clone();
                App.Config.Save();
                ValidateText.Text = Loc.T("Common.Success");

                if (apply)
                {
                    ApplyRuntime(cur);
                }
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Config.SaveFailed", ex.Message), Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void ApplyRuntime(AppSettings s)
        {
            try
            {
                // language
                I18nService.Instance.SetLanguage(s.Language);

                // pin -> controller
                try { if (App.Controller != null) App.Controller.ApplyPinFromConfig(); } catch { }
                // Idle threshold
                try { App.ScreenLockMod?.SetIdleMinutes(s.IdleMinutes); } catch { }
                // AutoStart
                AutoStartService.Sync(s.AutoStart);
                // Tasks global switch
                if (App.TaskScheduler != null)
                {
                    App.TaskScheduler.SetGlobalEnabled(s.TasksEnabled);
                }
                // Process exclusion cache
                try { ProcessExclusionService.InvalidateCache(); } catch { }
                // Update tray text/menu if available
                var app = Application.Current as App;
                if (app != null)
                {
                    try { app.Dispatcher.Invoke(new Action(() => app.RefreshMenuChecks())); } catch { }
                    try { app.Dispatcher.Invoke(new Action(() => app.UpdateTrayText())); } catch { }
                    try { app.Dispatcher.Invoke(new Action(() => App.TrayMenu?.RefreshStatus())); } catch { }
                }
            }
            catch { }
        }

        private void OnPinChangeClick(object sender, RoutedEventArgs e)
        {
            // verify old pin first if exists
            if (_editing.HasPin())
            {
                var verify = new VerifyPinWindow(App.Controller, Loc.T("Config.VerifyOldPinPrompt"));
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
