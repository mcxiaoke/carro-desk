using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScreenLock.Models;
using ScreenLock.Services;

namespace ScreenLock.Views
{
    public partial class ConfigEditorWindow : Window
    {
        private AppSettings _editing;

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
                var cur = App.Config.Current;
                _editing = cur.Clone();

                PathText.Text = ConfigService.FilePath;
                ModeText.Text = ConfigService.IsPortableMode ? "便携模式（exe 目录\\app_data）" : "漫游模式（%AppData%\\ScreenLock）";

                // IdleMinutes -> ComboBox (editable)
                string idleStr = _editing.IdleMinutes.ToString();
                bool found = false;
                for (int i = 0; i < IdleBox.Items.Count; i++)
                {
                    var item = IdleBox.Items[i] as ComboBoxItem;
                    if (item != null)
                    {
                        string txt = item.Content.ToString();
                        // "0 - 禁用" or "5"
                        string num = txt.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)[0];
                        if (num == idleStr) { IdleBox.SelectedIndex = i; found = true; break; }
                    }
                }
                if (!found) { IdleBox.Text = idleStr; IdleBox.SelectedIndex = -1; }
                else IdleBox.Text = idleStr;

                ShowClockBox.IsChecked = _editing.ShowClock;
                OpacitySlider.Value = _editing.OverlayOpacity;
                int initPct = (int)Math.Round(_editing.OverlayOpacity * 100);
                string initDesc = initPct >= 95 ? "全遮挡" : (initPct >= 80 ? "微透" : "半透");
                OpacityText.Text = string.Format("{0}% ({1})", initPct, initDesc);
                AutoStartBox.IsChecked = _editing.AutoStart;
                UnlockOnResumeBox.IsChecked = _editing.UnlockOnResume;
                TasksEnabledBox.IsChecked = _editing.TasksEnabled;

                ExcludeList.ItemsSource = null;
                var list = _editing.ExcludeProcesses != null ? new List<string>(_editing.ExcludeProcesses) : new List<string>();
                ExcludeList.ItemsSource = list;

                PinStatusText.Text = _editing.HasPin() ? "已设置（" + MaskHash(_editing.PinHash) + "）" : "未设置";
                ValidateText.Text = "";
            }
            catch (Exception ex)
            {
                MessageBox.Show("加载配置失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    menu.Items.Add(new MenuItem { Header = "暂无检测到的前台窗口进程", IsEnabled = false });
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
                MessageBox.Show("获取运行进程失败: " + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            if (MessageBox.Show("确定要将除 PIN 以外的配置恢复为默认值吗？", "恢复默认值", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var def = new AppSettings();
            IdleBox.Text = def.IdleMinutes.ToString();
            ShowClockBox.IsChecked = def.ShowClock;
            OpacitySlider.Value = def.OverlayOpacity;
            int defPct = (int)Math.Round(def.OverlayOpacity * 100);
            string defDesc = defPct >= 95 ? "全遮挡" : (defPct >= 80 ? "微透" : "半透");
            OpacityText.Text = string.Format("{0}% ({1})", defPct, defDesc);
            AutoStartBox.IsChecked = def.AutoStart;
            UnlockOnResumeBox.IsChecked = def.UnlockOnResume;
            TasksEnabledBox.IsChecked = def.TasksEnabled;
            ExcludeList.ItemsSource = null;
            ExcludeList.ItemsSource = new List<string>();
            ValidateText.Text = "已恢复默认值（请点击保存生效）";
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (OpacityText != null)
            {
                int pct = (int)Math.Round(e.NewValue * 100);
                string desc = pct >= 95 ? "全遮挡" : (pct >= 80 ? "微透" : "半透");
                OpacityText.Text = string.Format("{0}% ({1})", pct, desc);
            }
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
            // IdleMinutes
            string idleRaw = IdleBox.Text.Trim();
            // handle "0 - 禁用" selected
            if (idleRaw.Contains("-")) idleRaw = idleRaw.Split('-')[0].Trim();
            int idle;
            if (!int.TryParse(idleRaw, out idle)) idle = _editing.IdleMinutes;
            s.IdleMinutes = idle;
            s.ShowClock = ShowClockBox.IsChecked == true;
            s.OverlayOpacity = Math.Round(OpacitySlider.Value, 2);
            s.AutoStart = AutoStartBox.IsChecked == true;
            s.UnlockOnResume = UnlockOnResumeBox.IsChecked == true;
            s.TasksEnabled = TasksEnabledBox.IsChecked == true;
            var excl = ExcludeList.ItemsSource as List<string>;
            s.ExcludeProcesses = excl != null ? new List<string>(excl) : new List<string>();
            // keep pin
            s.PinSalt = _editing.PinSalt;
            s.PinHash = _editing.PinHash;
            return s;
        }

        private string Validate(AppSettings s)
        {
            if (s.IdleMinutes < 0 || s.IdleMinutes > 24 * 60) return "空闲分钟需在 0-1440 之间";
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
                ValidateText.Text = "校验失败: " + err;
                MessageBox.Show(err, "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                ValidateText.Text = "校验通过";
                MessageBox.Show("校验通过", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (!DoSave(false)) return;
            MessageBox.Show("已保存到 " + ConfigService.FilePath, "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnSaveApplyClick(object sender, RoutedEventArgs e)
        {
            if (!DoSave(true)) return;
            MessageBox.Show("已保存并应用", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private bool DoSave(bool apply)
        {
            var cur = BuildCurrent();
            string err = Validate(cur);
            if (err != null)
            {
                ValidateText.Text = "校验失败: " + err;
                MessageBox.Show(err, "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            try
            {
                // apply to global config
                cur.CopyTo(App.Config.Current);
                // also update editing copy (for pin)
                _editing = App.Config.Current.Clone();
                App.Config.Save();
                ValidateText.Text = "已保存";

                if (apply)
                {
                    ApplyRuntime(cur);
                }
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void ApplyRuntime(AppSettings s)
        {
            try
            {
                // pin -> controller
                try { if (App.Controller != null) App.Controller.ApplyPinFromConfig(); } catch { }
                // Idle threshold
                if (App.Idle != null)
                {
                    App.Idle.Threshold = TimeSpan.FromMinutes(s.IdleMinutes);
                    App.Idle.Reset();
                }
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
                var verify = new VerifyPinWindow(App.Controller, "修改 PIN 需先验证原 PIN");
                verify.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                verify.Owner = this;
                if (verify.ShowDialog() != true)
                    return;
            }
            var first = new FirstRunWindow();
            first.Title = "设置新 PIN";
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
                PinStatusText.Text = "已设置（" + MaskHash(_editing.PinHash) + "）*未保存";
                ValidateText.Text = "PIN 已修改，请保存";
                MessageBox.Show("新 PIN 已生成，点 保存 后生效", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) { Close(); }
    }
}
