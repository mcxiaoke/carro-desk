using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CarroDesk.Core;
using CarroDesk.Models;
using CarroDesk.Services.Localization;
using CarroDesk.Services.Tasks;

namespace CarroDesk.Views
{
    public partial class TaskEditorWindow : Window
    {
        private List<TaskDefinition> _tasks = new List<TaskDefinition>();
        private bool _isUpdating = false;
        private readonly ITaskSchedulerService _scheduler;
        private readonly Action _onReloadCompleted;

        public TaskEditorWindow(ITaskSchedulerService scheduler = null, Action onReloadCompleted = null)
        {
            _scheduler = scheduler;
            _onReloadCompleted = onReloadCompleted;
            InitializeComponent();
            Loaded += OnLoaded;
            I18nService.Instance.LanguageChanged += OnLanguageChanged;
            Closed += OnClosed;
        }

        private void OnLanguageChanged()
        {
            Dispatcher.Invoke(() =>
            {
                RefreshList();
                RefreshScriptQuick();
                InitTemplateQuick();
                UpdateCronHint();
            });
        }

        private void OnClosed(object sender, EventArgs e)
        {
            I18nService.Instance.LanguageChanged -= OnLanguageChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadTasks();
            RefreshScriptQuick();
            InitTemplateQuick();
            if (_tasks.Count > 0) TaskList.SelectedIndex = 0;
        }

        private void LoadTasks()
        {
            try
            {
                var res = TaskConfigService.Load();
                _tasks = res.Tasks ?? new List<TaskDefinition>();
                if (_tasks.Count == 0 && res.Errors.Count > 0)
                {
                    MessageBox.Show(Loc.T("Tasks.LoadErrors", string.Join("\n", res.Errors)), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                RefreshList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Tasks.LoadFailed", ex.Message), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                _tasks = new List<TaskDefinition>();
                RefreshList();
            }
        }

        private void RefreshList()
        {
            var sel = TaskList.SelectedIndex;
            TaskList.ItemsSource = null;
            TaskList.ItemsSource = _tasks;
            if (EmptyTasksHint != null) EmptyTasksHint.Visibility = _tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (sel >= 0 && sel < _tasks.Count) TaskList.SelectedIndex = sel;
            else if (_tasks.Count > 0) TaskList.SelectedIndex = 0;
            else ClearForm();
        }

        private void RefreshScriptQuick()
        {
            try
            {
                _isUpdating = true;
                ScriptQuickBox.Items.Clear();
                var hdr = new ComboBoxItem { Content = Loc.T("Tasks.ActionScriptQuick", "scripts/ 快速选择"), IsEnabled = false };
                ScriptQuickBox.Items.Add(hdr);
                var dir = Services.ConfigService.ScriptsDirPath;
                if (Directory.Exists(dir))
                {
                    var files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly);
                    foreach (var f in files.OrderBy(x => x))
                    {
                        var name = System.IO.Path.GetFileName(f);
                        var item = new ComboBoxItem { Content = name, Tag = f };
                        ScriptQuickBox.Items.Add(item);
                    }
                    var subs = Directory.GetDirectories(dir);
                    foreach (var sub in subs)
                    {
                        var files2 = Directory.GetFiles(sub, "*.*", SearchOption.TopDirectoryOnly);
                        foreach (var f in files2.OrderBy(x => x))
                        {
                            var rel = f.Substring(dir.Length).TrimStart('\\', '/');
                            var item = new ComboBoxItem { Content = rel, Tag = f };
                            ScriptQuickBox.Items.Add(item);
                        }
                    }
                }
                ScriptQuickBox.SelectedIndex = 0;
            }
            catch { }
            finally
            {
                _isUpdating = false;
            }
        }

        private void ScriptQuickBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdating) return;
            var item = ScriptQuickBox.SelectedItem as ComboBoxItem;
            if (item == null || item.Tag == null) return;
            string full = item.Tag.ToString();
            string rel = item.Content.ToString();
            // if scripts dir, we can use bare name
            FileBox.Text = rel;
            // auto fill workdir if empty
            if (string.IsNullOrWhiteSpace(WorkDirBox.Text))
            {
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir)) WorkDirBox.Text = dir;
                }
                catch { }
            }
            ScriptQuickBox.SelectedIndex = 0;
        }

        private void InitTemplateQuick()
        {
            try
            {
                TemplateQuickBox.Items.Clear();
                TemplateQuickBox.Items.Add(new ComboBoxItem { Content = Loc.T("Tasks.InsertTemplate", "插入模板..."), IsEnabled = false, IsSelected = true });
                TemplateQuickBox.Items.Add(new ComboBoxItem { Content = "{{date}} - 日期 (yyyy-MM-dd)", Tag = "{{date}}" });
                TemplateQuickBox.Items.Add(new ComboBoxItem { Content = "{{time}} - 时间 (HH-mm-ss)", Tag = "{{time}}" });
                TemplateQuickBox.Items.Add(new ComboBoxItem { Content = "{{datetime}} - 日期时间", Tag = "{{datetime}}" });
                TemplateQuickBox.Items.Add(new ComboBoxItem { Content = "{{timestamp}} - 紧凑时间戳", Tag = "{{timestamp}}" });
                TemplateQuickBox.Items.Add(new ComboBoxItem { Content = "{{task}} - 任务名称", Tag = "{{task}}" });
                TemplateQuickBox.Items.Add(new ComboBoxItem { Content = "{{scripts}} - 脚本目录", Tag = "{{scripts}}" });
                TemplateQuickBox.Items.Add(new ComboBoxItem { Content = "{{logs}} - 日志目录", Tag = "{{logs}}" });
                TemplateQuickBox.SelectedIndex = 0;
            }
            catch { }
        }

        private void ClearForm()
        {
            _isUpdating = true;
            NameBox.Text = "";
            EnabledBox.IsChecked = true;
            TriggerTypeBox.SelectedIndex = 0;
            DelayBox.Text = "5";
            EveryBox.Text = "";
            AtBox.Text = "";
            CronBox.Text = "";
            IdleBox.Text = "10";
            HotkeyBox.Text = "";
            WatchPathBox.Text = "";
            WatchFilterBox.Text = "*.*";
            WatchEventBox.SelectedIndex = 0;
            FileBox.Text = "";
            ArgsBox.Text = "";
            WorkDirBox.Text = "";
            HiddenBox.IsChecked = true;
            AllowConcurrentBox.IsChecked = false;
            NotifyBox.IsChecked = true;
            TimeoutBox.Text = "0";
            RetryBox.Text = "0";
            OnlyIdleBox.IsChecked = false;
            AcPowerBox.IsChecked = false;
            NetworkBox.IsChecked = false;
            FileExistsBox.Text = "";
            FileNotExistsBox.Text = "";
            ValidateText.Text = "";
            UpdateTriggerPanels();
            _isUpdating = false;
        }

        private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdating) return;
            var task = TaskList.SelectedItem as TaskDefinition;
            if (task == null) return;
            _isUpdating = true;
            NameBox.Text = task.Name;
            EnabledBox.IsChecked = task.Enabled;
            // trigger type
            string tag = task.Trigger.RawType != "" ? task.Trigger.RawType : task.Trigger.Type.ToString().ToLowerInvariant();
            SelectTriggerTag(tag);
            DelayBox.Text = task.Trigger.DelaySec.ToString();
            EveryBox.Text = !string.IsNullOrWhiteSpace(task.Trigger.Every) ? task.Trigger.Every : (task.Trigger.EverySec > 0 ? task.Trigger.EverySec.ToString() : "");
            AtBox.Text = task.Trigger.At;
            CronBox.Text = task.Trigger.Expr;
            IdleBox.Text = task.Trigger.AfterMinutes.ToString();
            HotkeyBox.Text = task.Trigger.Hotkey;
            WatchPathBox.Text = task.Trigger.WatchPath;
            WatchFilterBox.Text = string.IsNullOrWhiteSpace(task.Trigger.WatchFilter) ? "*.*" : task.Trigger.WatchFilter;
            SelectWatchEvent(task.Trigger.WatchEvent);
            FileBox.Text = task.Action.File;
            ArgsBox.Text = task.Action.Args;
            WorkDirBox.Text = task.Action.WorkDir != "" ? task.Action.WorkDir : (task.Options.WorkDir != "" ? task.Options.WorkDir : "");
            HiddenBox.IsChecked = task.Options.Hidden;
            AllowConcurrentBox.IsChecked = task.Options.AllowConcurrent;
            NotifyBox.IsChecked = task.Options.NotifyOnFailure;
            TimeoutBox.Text = task.Options.TimeoutSec.ToString();
            RetryBox.Text = task.Options.Retry.ToString();
            OnlyIdleBox.IsChecked = task.When.OnlyIdle;
            AcPowerBox.IsChecked = task.When.AcPower;
            NetworkBox.IsChecked = task.When.NetworkAvailable;
            FileExistsBox.Text = task.When.FileExists;
            FileNotExistsBox.Text = task.When.FileNotExists;
            ValidateText.Text = "";
            UpdateTriggerPanels();
            _isUpdating = false;
            UpdateCronHint();
        }

        private void SelectTriggerTag(string tag)
        {
            tag = (tag ?? "").ToLowerInvariant();
            for (int i = 0; i < TriggerTypeBox.Items.Count; i++)
            {
                var it = TriggerTypeBox.Items[i] as ComboBoxItem;
                if (it != null && (it.Tag.ToString().ToLowerInvariant() == tag)) { TriggerTypeBox.SelectedIndex = i; return; }
            }
            // fallback map
            if (tag == "start" || tag == "boot") tag = "startup";
            for (int i = 0; i < TriggerTypeBox.Items.Count; i++)
            {
                var it = TriggerTypeBox.Items[i] as ComboBoxItem;
                if (it != null && it.Tag.ToString().ToString() == tag) { TriggerTypeBox.SelectedIndex = i; return; }
            }
            TriggerTypeBox.SelectedIndex = 0;
        }

        private void SelectWatchEvent(string evt)
        {
            evt = (evt ?? "created").ToLowerInvariant();
            for (int i = 0; i < WatchEventBox.Items.Count; i++)
            {
                var it = WatchEventBox.Items[i] as ComboBoxItem;
                if (it != null && it.Tag.ToString() == evt) { WatchEventBox.SelectedIndex = i; return; }
            }
            WatchEventBox.SelectedIndex = 0;
        }

        private void TriggerTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateTriggerPanels();
        }

        private void UpdateTriggerPanels()
        {
            PanelStartup.Visibility = Visibility.Collapsed;
            PanelInterval.Visibility = Visibility.Collapsed;
            PanelDaily.Visibility = Visibility.Collapsed;
            PanelCron.Visibility = Visibility.Collapsed;
            PanelIdle.Visibility = Visibility.Collapsed;
            PanelHotkey.Visibility = Visibility.Collapsed;
            PanelWatch.Visibility = Visibility.Collapsed;
            PanelManual.Visibility = Visibility.Collapsed;
            var sel = TriggerTypeBox.SelectedItem as ComboBoxItem;
            if (sel == null) return;
            string tag = sel.Tag.ToString();
            switch (tag)
            {
                case "startup": PanelStartup.Visibility = Visibility.Visible; break;
                case "interval": PanelInterval.Visibility = Visibility.Visible; break;
                case "daily": PanelDaily.Visibility = Visibility.Visible; break;
                case "cron": PanelCron.Visibility = Visibility.Visible; break;
                case "idle": PanelIdle.Visibility = Visibility.Visible; break;
                case "hotkey": PanelHotkey.Visibility = Visibility.Visible; break;
                case "watch": PanelWatch.Visibility = Visibility.Visible; break;
                case "manual": PanelManual.Visibility = Visibility.Visible; break;
                default: break;
            }
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            int idx = _tasks.Count + 1;
            string newName = "new-task-" + idx;
            while (_tasks.Any(x => string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                idx++;
                newName = "new-task-" + idx;
            }

            var newTask = new TaskDefinition
            {
                Name = newName,
                Enabled = true,
                Trigger = new TaskTrigger { Type = TaskTriggerType.Startup, RawType = "startup", DelaySec = 5 },
                Action = new TaskAction { File = "hello.js" },
                Options = new TaskOptions { Hidden = true }
            };
            _tasks.Add(newTask);
            RefreshList();
            TaskList.SelectedItem = newTask;
            NameBox.Focus();
            NameBox.SelectAll();
        }

        private void OnDuplicateClick(object sender, RoutedEventArgs e)
        {
            var cur = TaskList.SelectedItem as TaskDefinition;
            if (cur == null) return;

            string copyName = cur.Name + "-copy";
            int idx = 1;
            while (_tasks.Any(x => string.Equals(x.Name, copyName, StringComparison.OrdinalIgnoreCase)))
            {
                idx++;
                copyName = cur.Name + "-copy" + idx;
            }

            var copy = new TaskDefinition
            {
                Name = copyName,
                Enabled = cur.Enabled,
                Trigger = new TaskTrigger
                {
                    Type = cur.Trigger.Type,
                    RawType = cur.Trigger.RawType,
                    DelaySec = cur.Trigger.DelaySec,
                    EverySec = cur.Trigger.EverySec,
                    Every = cur.Trigger.Every,
                    At = cur.Trigger.At,
                    Expr = cur.Trigger.Expr,
                    AfterMinutes = cur.Trigger.AfterMinutes,
                    Hotkey = cur.Trigger.Hotkey,
                    WatchPath = cur.Trigger.WatchPath,
                    WatchFilter = cur.Trigger.WatchFilter,
                    WatchEvent = cur.Trigger.WatchEvent
                },
                Action = new TaskAction
                {
                    File = cur.Action.File,
                    Args = cur.Action.Args,
                    WorkDir = cur.Action.WorkDir
                },
                Options = new TaskOptions
                {
                    Hidden = cur.Options.Hidden,
                    TimeoutSec = cur.Options.TimeoutSec,
                    AllowConcurrent = cur.Options.AllowConcurrent,
                    Retry = cur.Options.Retry,
                    NotifyOnFailure = cur.Options.NotifyOnFailure,
                    WorkDir = cur.Options.WorkDir
                },
                When = new TaskCondition
                {
                    OnlyIdle = cur.When.OnlyIdle,
                    AcPower = cur.When.AcPower,
                    NetworkAvailable = cur.When.NetworkAvailable,
                    FileExists = cur.When.FileExists,
                    FileNotExists = cur.When.FileNotExists
                }
            };
            _tasks.Add(copy);
            RefreshList();
            TaskList.SelectedItem = copy;
            NameBox.Focus();
            NameBox.SelectAll();
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var cur = TaskList.SelectedItem as TaskDefinition;
            if (cur == null) return;
            if (MessageBox.Show(Loc.T("Tasks.DeleteConfirm", cur.Name), Loc.T("Common.Confirm", "确认"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _tasks.Remove(cur);
            RefreshList();
        }

        private void OnBrowseFileClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog();
                dlg.Filter = "脚本/可执行|*.ps1;*.js;*.py;*.bat;*.cmd;*.vbs;*.exe|所有文件|*.*";
                string scripts = Services.ConfigService.ScriptsDirPath;
                if (Directory.Exists(scripts)) dlg.InitialDirectory = scripts;
                if (dlg.ShowDialog() == true)
                {
                    string full = dlg.FileName;
                    string scriptsDir = Services.ConfigService.ScriptsDirPath;
                    string rel = full;
                    try
                    {
                        if (full.StartsWith(scriptsDir, StringComparison.OrdinalIgnoreCase))
                            rel = full.Substring(scriptsDir.Length).TrimStart('\\', '/');
                    }
                    catch { }
                    FileBox.Text = rel;
                    if (string.IsNullOrWhiteSpace(WorkDirBox.Text))
                    {
                        try { WorkDirBox.Text = System.IO.Path.GetDirectoryName(full); } catch { }
                    }
                }
            }
            catch { }
        }

        private void OnBrowseDirClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog();
                dlg.Description = Loc.T("Tasks.SelectWorkDir", "选择工作目录");
                string scripts = Services.ConfigService.ScriptsDirPath;
                if (Directory.Exists(scripts)) dlg.SelectedPath = scripts;
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    WorkDirBox.Text = dlg.SelectedPath;
                }
            }
            catch { }
        }

        private void OnWatchBrowseClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog();
                dlg.Description = Loc.T("Tasks.SelectWatchDir", "选择监听目录");
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) WatchPathBox.Text = dlg.SelectedPath;
            }
            catch { }
        }

        private TaskDefinition BuildCurrent()
        {
            var t = new TaskDefinition();
            t.Name = NameBox.Text.Trim();
            t.Enabled = EnabledBox.IsChecked == true;
            var sel = TriggerTypeBox.SelectedItem as ComboBoxItem;
            string tag = sel != null ? sel.Tag.ToString() : "startup";
            t.Trigger.RawType = tag;
            t.Trigger.Type = ParseType(tag);
            int iv;
            if (int.TryParse(DelayBox.Text.Trim(), out iv)) t.Trigger.DelaySec = iv;
            string every = EveryBox.Text.Trim();
            if (!string.IsNullOrEmpty(every))
            {
                int sec;
                if (int.TryParse(every, out sec)) t.Trigger.EverySec = sec;
                else t.Trigger.Every = every;
            }
            t.Trigger.At = AtBox.Text.Trim();
            t.Trigger.Expr = CronBox.Text.Trim();
            int idleM;
            if (int.TryParse(IdleBox.Text.Trim(), out idleM)) t.Trigger.AfterMinutes = idleM;
            t.Trigger.Hotkey = HotkeyBox.Text.Trim();
            t.Trigger.WatchPath = WatchPathBox.Text.Trim();
            t.Trigger.WatchFilter = WatchFilterBox.Text.Trim();
            var wSel = WatchEventBox.SelectedItem as ComboBoxItem;
            t.Trigger.WatchEvent = wSel != null ? wSel.Tag.ToString() : "created";
            t.Action.File = FileBox.Text.Trim();
            t.Action.Args = ArgsBox.Text.Trim();
            t.Action.WorkDir = "";
            string wd = WorkDirBox.Text.Trim();
            t.Options.WorkDir = wd;
            t.Options.Hidden = HiddenBox.IsChecked == true;
            t.Options.AllowConcurrent = AllowConcurrentBox.IsChecked == true;
            t.Options.NotifyOnFailure = NotifyBox.IsChecked == true;
            int to, rt;
            if (int.TryParse(TimeoutBox.Text.Trim(), out to)) t.Options.TimeoutSec = to;
            if (int.TryParse(RetryBox.Text.Trim(), out rt)) t.Options.Retry = rt;
            t.When.OnlyIdle = OnlyIdleBox.IsChecked == true;
            t.When.AcPower = AcPowerBox.IsChecked == true;
            t.When.NetworkAvailable = NetworkBox.IsChecked == true;
            t.When.FileExists = FileExistsBox.Text.Trim();
            t.When.FileNotExists = FileNotExistsBox.Text.Trim();
            return t;
        }

        private TaskTriggerType ParseType(string tag)
        {
            tag = tag.ToLowerInvariant();
            switch (tag)
            {
                case "startup": return TaskTriggerType.Startup;
                case "interval": return TaskTriggerType.Interval;
                case "daily": return TaskTriggerType.Daily;
                case "cron": return TaskTriggerType.Cron;
                case "sessionlock": return TaskTriggerType.SessionLock;
                case "sessionunlock": return TaskTriggerType.SessionUnlock;
                case "idle": return TaskTriggerType.Idle;
                case "manual": return TaskTriggerType.Manual;
                case "hotkey": return TaskTriggerType.Hotkey;
                case "watch": return TaskTriggerType.Watch;
                default: return TaskTriggerType.Manual;
            }
        }

        private bool ValidateForm(TaskDefinition built, TaskDefinition cur, out string error, out Control controlToFocus)
        {
            error = null;
            controlToFocus = null;

            if (string.IsNullOrWhiteSpace(built.Name))
            {
                error = Loc.T("Tasks.ValNameRequired", "请输入任务名称");
                controlToFocus = NameBox;
                return false;
            }
            if (built.Name.Length > 64)
            {
                error = Loc.T("Tasks.ValNameTooLong", "任务名称过长 (最多 64 个字符)");
                controlToFocus = NameBox;
                return false;
            }
            foreach (char c in built.Name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok)
                {
                    error = Loc.T("Tasks.ValNameInvalidChar", c);
                    controlToFocus = NameBox;
                    return false;
                }
            }

            // 查重：任务名称在其它任务中不可重复
            bool duplicate = _tasks.Any(t => t != cur && string.Equals(t.Name, built.Name, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                error = Loc.T("Tasks.ValNameDuplicate", built.Name);
                controlToFocus = NameBox;
                return false;
            }

            if (built.Trigger == null)
            {
                error = Loc.T("Tasks.ValTriggerRequired", "请选择触发器类型");
                controlToFocus = TriggerTypeBox;
                return false;
            }

            if (string.IsNullOrWhiteSpace(built.Action.File))
            {
                error = Loc.T("Tasks.ValFileRequired", "请指定要执行的脚本或程序文件 (Action.File)");
                controlToFocus = FileBox;
                return false;
            }

            if (built.Trigger.Type == TaskTriggerType.Interval)
            {
                int sec = built.Trigger.EverySec;
                if (sec <= 0 && !string.IsNullOrWhiteSpace(built.Trigger.Every))
                    sec = TaskDefinition.ParseDuration(built.Trigger.Every);
                if (sec <= 0)
                {
                    error = Loc.T("Tasks.ValIntervalInvalid", "定时间隔必须大于 0 秒 (例如 30s, 5m, 1h)");
                    controlToFocus = EveryBox;
                    return false;
                }
            }
            else if (built.Trigger.Type == TaskTriggerType.Daily)
            {
                if (string.IsNullOrWhiteSpace(built.Trigger.At))
                {
                    error = Loc.T("Tasks.ValDailyRequired", "每天定时必须指定时间 (格式 HH:mm，如 09:00)");
                    controlToFocus = AtBox;
                    return false;
                }
                TimeSpan t;
                if (!TaskDefinition.TryParseTime(built.Trigger.At, out t))
                {
                    error = Loc.T("Tasks.ValDailyInvalid", built.Trigger.At);
                    controlToFocus = AtBox;
                    return false;
                }
            }
            else if (built.Trigger.Type == TaskTriggerType.Cron)
            {
                if (string.IsNullOrWhiteSpace(built.Trigger.Expr))
                {
                    error = Loc.T("Tasks.ValCronRequired", "Cron 表达式不能为空 (如 0 9 * * 1)");
                    controlToFocus = CronBox;
                    return false;
                }
                string cronErr;
                if (!CronHelper.Validate(built.Trigger.Expr, out cronErr))
                {
                    error = Loc.T("Tasks.ValCronInvalid", cronErr);
                    controlToFocus = CronBox;
                    return false;
                }
            }
            else if (built.Trigger.Type == TaskTriggerType.Idle)
            {
                if (built.Trigger.AfterMinutes <= 0)
                {
                    error = Loc.T("Tasks.ValIdleInvalid", "系统空闲等待时间必须大于 0 分钟");
                    controlToFocus = IdleBox;
                    return false;
                }
            }
            else if (built.Trigger.Type == TaskTriggerType.Hotkey)
            {
                if (string.IsNullOrWhiteSpace(built.Trigger.Hotkey))
                {
                    error = Loc.T("Tasks.ValHotkeyRequired", "热键不能为空 (例如 Ctrl+Alt+Q)");
                    controlToFocus = HotkeyBox;
                    return false;
                }
                string hkErr;
                if (!HotkeyHelper.Validate(built.Trigger.Hotkey, out hkErr))
                {
                    error = Loc.T("Tasks.ValHotkeyInvalid", hkErr);
                    controlToFocus = HotkeyBox;
                    return false;
                }
            }
            else if (built.Trigger.Type == TaskTriggerType.Watch)
            {
                if (string.IsNullOrWhiteSpace(built.Trigger.WatchPath))
                {
                    error = Loc.T("Tasks.ValWatchPathRequired", "文件监听目录不能为空");
                    controlToFocus = WatchPathBox;
                    return false;
                }
            }

            if (built.Options != null && built.Options.TimeoutSec < 0)
            {
                error = Loc.T("Tasks.ValTimeoutNegative", "超时时间不能为负数");
                controlToFocus = TimeoutBox;
                return false;
            }
            if (built.Options != null && built.Options.Retry < 0)
            {
                error = Loc.T("Tasks.ValRetryNegative", "重试次数不能为负数");
                controlToFocus = RetryBox;
                return false;
            }

            return true;
        }

        private void FocusInput(Control ctrl)
        {
            if (ctrl is TextBox tb)
            {
                tb.Focus();
                tb.SelectAll();
            }
            else if (ctrl != null)
            {
                ctrl.Focus();
            }
        }

        private void OnValidateClick(object sender, RoutedEventArgs e)
        {
            var cur = TaskList.SelectedItem as TaskDefinition;
            var built = BuildCurrent();
            string err;
            Control focusCtrl;
            if (!ValidateForm(built, cur, out err, out focusCtrl))
            {
                ValidateText.Text = Loc.T("Tasks.ValFailPrefix", "校验失败: ") + err;
                FocusInput(focusCtrl);
                MessageBox.Show(err, Loc.T("Config.ValidationFailed", "校验失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                ValidateText.Text = "✓ " + Loc.T("Config.ValidationPassed", "校验通过");
                MessageBox.Show(Loc.T("Tasks.ValPassedMsg", "当前任务配置合法有效！"), Loc.T("Config.ValidationPassed", "校验通过"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private bool SaveTasksInternal()
        {
            var cur = TaskList.SelectedItem as TaskDefinition;
            var built = BuildCurrent();

            // 如果当前无任何任务且表单完全为空，无需保存
            if (cur == null && _tasks.Count == 0 && string.IsNullOrWhiteSpace(built.Name) && string.IsNullOrWhiteSpace(built.Action.File))
            {
                MessageBox.Show(Loc.T("Tasks.NoTasksNeedSave", "当前没有需要保存的任务。"), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            // 1. 先校验当前正在编辑的表单项，校验未通过前绝不修改内存列表
            string err;
            Control focusCtrl;
            if (!ValidateForm(built, cur, out err, out focusCtrl))
            {
                ValidateText.Text = Loc.T("Tasks.ValFailPrefix", "校验失败: ") + err;
                FocusInput(focusCtrl);
                MessageBox.Show(Loc.T("Tasks.CurrentTaskError", err), Loc.T("Config.ValidationFailed", "校验失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // 2. 检查内存中已有的其他任务合法性（防止脏数据存盘）
            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _tasks)
            {
                if (t == cur) continue;
                string terr = t.Validate();
                if (terr != null) errors.Add(t.Name + ": " + terr);
                if (seen.Contains(t.Name)) errors.Add("重名: " + t.Name);
                else seen.Add(t.Name);
            }
            if (errors.Count > 0)
            {
                MessageBox.Show(Loc.T("Tasks.OtherTasksError", string.Join("\n", errors)), Loc.T("Config.ValidationFailed", "校验失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // 3. 校验通过，原子性提交到任务列表中
            TaskDefinition targetTask = cur;
            if (targetTask == null)
            {
                targetTask = built;
                _tasks.Add(targetTask);
            }
            else
            {
                targetTask.Name = built.Name;
                targetTask.Enabled = built.Enabled;
                targetTask.Trigger = built.Trigger;
                targetTask.Action = built.Action;
                targetTask.Options = built.Options;
                targetTask.When = built.When;
            }

            // 4. 持久化到 tasks.json 磁盘文件
            try
            {
                TaskConfigService.Save(_tasks);
                ValidateText.Text = "✓ " + Loc.T("Tasks.SavedAt", DateTime.Now.ToString("HH:mm:ss"));

                // 刷新左侧列表展示（徽标、状态圆点、名称）并保持当前选中项
                _isUpdating = true;
                TaskList.ItemsSource = null;
                TaskList.ItemsSource = _tasks;
                if (EmptyTasksHint != null) EmptyTasksHint.Visibility = _tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                TaskList.SelectedItem = targetTask;
                _isUpdating = false;

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Tasks.SaveFailed", ex.Message), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (SaveTasksInternal())
            {
                MessageBox.Show(Loc.T("Tasks.SaveSuccess", Services.ConfigService.TaskFilePath), Loc.T("Common.Success", "保存成功"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnSaveReloadClick(object sender, RoutedEventArgs e)
        {
            if (!SaveTasksInternal()) return;
            try
            {
                var scheduler = _scheduler;
                if (scheduler != null)
                {
                    var res = scheduler.Reload();
                    string msg = res.Errors == 0 ? Loc.T("Tasks.ReloadSuccess", res.Tasks) : Loc.T("Tasks.ReloadWithErrors", res.Errors);
                    MessageBox.Show(msg, Loc.T("Tray.ReloadTasks", "重载任务"), MessageBoxButton.OK, MessageBoxImage.Information);
                    try { _onReloadCompleted?.Invoke(); } catch { }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Tasks.ReloadFailed", ex.Message), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) { Close(); }

        private System.Windows.Threading.DispatcherTimer _cronDebounceTimer;

        private void CronBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating) return;
            if (_cronDebounceTimer == null)
            {
                _cronDebounceTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(300)
                };
                _cronDebounceTimer.Tick += (s, args) =>
                {
                    _cronDebounceTimer.Stop();
                    UpdateCronHint();
                };
            }
            _cronDebounceTimer.Stop();
            _cronDebounceTimer.Start();
        }

        private void UpdateCronHint()
        {
            if (CronHintText == null || CronBox == null) return;
            string expr = CronBox.Text.Trim();
            if (string.IsNullOrEmpty(expr))
            {
                CronHintText.Text = Loc.T("Tasks.CronHintDefault", "分 时 日 月 周 (5个字段)，如 0 9 * * 1");
                CronHintText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
                return;
            }
            string err;
            if (!CronHelper.Validate(expr, out err))
            {
                CronHintText.Text = Loc.T("Tasks.CronFormatError", err);
                CronHintText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x30, 0x25));
            }
            else
            {
                string desc = CronHelper.ExplainCron(expr);
                var next = CronHelper.GetNextOccurrence(expr, DateTime.Now);
                string nextStr = next.HasValue ? Loc.T("Tasks.CronHintNext", next.Value) : "";
                CronHintText.Text = "✓ " + desc + nextStr;
                CronHintText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x05, 0x96, 0x69));
            }
        }

        private void TemplateQuickBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdating) return;
            var item = TemplateQuickBox.SelectedItem as ComboBoxItem;
            if (item == null || item.Tag == null) return;
            string tag = item.Tag.ToString();
            if (!string.IsNullOrEmpty(tag))
            {
                if (string.IsNullOrEmpty(ArgsBox.Text)) ArgsBox.Text = tag;
                else ArgsBox.Text += " " + tag;
                ArgsBox.Focus();
                ArgsBox.CaretIndex = ArgsBox.Text.Length;
            }
            TemplateQuickBox.SelectedIndex = 0;
        }

        private async void OnTestRunClick(object sender, RoutedEventArgs e)
        {
            var selected = TaskList.SelectedItem as TaskDefinition;
            var cur = BuildCurrent();
            string err;
            Control focusCtrl;
            if (!ValidateForm(cur, selected, out err, out focusCtrl))
            {
                ValidateText.Text = Loc.T("Tasks.ValFailPrefix", "校验失败: ") + err;
                FocusInput(focusCtrl);
                MessageBox.Show(Loc.T("Tasks.TestError", err), Loc.T("Config.ValidationFailed", "校验失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ValidateText.Text = Loc.T("Tasks.TestRunning", "正在运行测试...");
            var btn = sender as Button;
            if (btn != null) btn.IsEnabled = false;

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int exitCode = await TaskRunner.RunAsync(cur, "manual-test").ConfigureAwait(true);
                sw.Stop();
                string status = exitCode == 0 ? Loc.T("Common.Success", "成功") : Loc.T("Tasks.FailedExitCode", exitCode);
                ValidateText.Text = Loc.T("Tasks.TestCompletedSummary", status, sw.Elapsed.TotalSeconds);
                MessageBox.Show(Loc.T("Tasks.TestResult", status, sw.Elapsed.TotalSeconds, cur.Name), Loc.T("Tasks.TestResultTitle", "测试结果"), MessageBoxButton.OK, exitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                ValidateText.Text = Loc.T("Tasks.TestException", ex.Message);
                MessageBox.Show(Loc.T("Tasks.TestException", ex.Message), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (btn != null) btn.IsEnabled = true;
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
        }
    }
}
