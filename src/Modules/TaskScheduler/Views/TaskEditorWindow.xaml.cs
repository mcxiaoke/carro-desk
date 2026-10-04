using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using CarroDesk.Core;
using CarroDesk.Models;
using CarroDesk.Services.Localization;
using CarroDesk.Services.Tasks;

namespace CarroDesk.Modules.TaskScheduler.Views
{
    public partial class TaskEditorWindow : Window
    {
        private List<TaskDefinition> _tasks = new List<TaskDefinition>();
        private List<string> _loadErrors = new List<string>();
        private bool _isUpdating = false;
        private readonly ITaskSchedulerService _scheduler;
        private readonly Action _onReloadCompleted;

        // 测试运行状态：活动标志同时是防重入闸（按钮、右键菜单两条入口共用）；
        // _testRunAbandoned 表示窗口关闭时放弃接收结果（进程已被取消终止）；
        // detach 任务的测试实例不进调度器注册表，句柄由编辑器全权持有（停止按钮/关窗即终止）
        private const int TestRunFallbackTimeoutSec = 10;
        private bool _testRunActive;
        private bool _testRunAbandoned;
        private CancellationTokenSource _testRunCts;
        private TaskProcessHandle _testRunHandle;

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
            if (_runStatusTimer != null) { try { _runStatusTimer.Stop(); } catch { } _runStatusTimer = null; }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            GlobalEnabledBox.IsChecked = _scheduler?.IsGlobalEnabled ?? true;
            LoadTasks();
            RefreshScriptQuick();
            InitTemplateQuick();
            if (_tasks.Count > 0) TaskList.SelectedIndex = 0;
            StartRunStatusTimer();
        }

        private void OnGlobalEnabledClick(object sender, RoutedEventArgs e)
        {
            if (_isUpdating) return;
            bool enabled = GlobalEnabledBox.IsChecked == true;
            bool success = _scheduler?.SetGlobalEnabled(enabled) ?? false;
            if (_scheduler != null) GlobalEnabledBox.IsChecked = _scheduler.IsGlobalEnabled;
            if (!success) return;
            _onReloadCompleted?.Invoke();
        }

        private void LoadTasks()
        {
            try
            {
                var res = TaskConfigService.LoadOrCreate();
                _tasks = res.Tasks ?? new List<TaskDefinition>();
                _loadErrors = res.Errors ?? new List<string>();
                if (_loadErrors.Count > 0)
                {
                    // 任意加载错误都必须在编辑器中可见。旧实现只在有效任务为 0 时提示，
                    // 随后保存会把非法/未来 schema 任务从 tasks.json 中永久删除。
                    MessageBox.Show(Loc.T("Tasks.LoadErrors", "加载 tasks.json 有错误:\n{0}", string.Join("\n", _loadErrors)), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                RefreshList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Tasks.LoadFailed", "加载失败: {0}", ex.Message), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                TemplateQuickBox.Items.Add(new ComboBoxItem { Content = Loc.T("Tasks.TemplateDate", "{{date}} - 日期 (yyyy-MM-dd)"), Tag = "{{date}}" });
                TemplateQuickBox.Items.Add(new ComboBoxItem { Content = Loc.T("Tasks.TemplateTime", "{{time}} - 时间 (HH-mm-ss)"), Tag = "{{time}}" });
                TemplateQuickBox.Items.Add(new ComboBoxItem { Content = Loc.T("Tasks.TemplateDateTime", "{{datetime}} - 日期时间"), Tag = "{{datetime}}" });
                TemplateQuickBox.Items.Add(new ComboBoxItem { Content = Loc.T("Tasks.TemplateTimestamp", "{{timestamp}} - 紧凑时间戳"), Tag = "{{timestamp}}" });
                TemplateQuickBox.Items.Add(new ComboBoxItem { Content = Loc.T("Tasks.TemplateTask", "{{task}} - 任务名称"), Tag = "{{task}}" });
                TemplateQuickBox.Items.Add(new ComboBoxItem { Content = Loc.T("Tasks.TemplateScripts", "{{scripts}} - 脚本目录"), Tag = "{{scripts}}" });
                TemplateQuickBox.Items.Add(new ComboBoxItem { Content = Loc.T("Tasks.TemplateLogs", "{{logs}} - 日志目录"), Tag = "{{logs}}" });
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
            string tag = TaskConfigService.GetCanonicalTriggerTag(task.Trigger.Type);
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
            SelectRunMode(task.Options.IsDetach ? "detach" : "wait");
            KillWithHostBox.IsChecked = task.Options.KillWithHost;
            RestartBox.IsChecked = task.Options.RestartOnFailure;
            RestartDelaySecBox.Text = task.Options.RestartDelaySec.ToString();
            RestartLimitBox.Text = task.Options.RestartLimit.ToString();
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
                    WorkDir = cur.Options.WorkDir,
                    Mode = cur.Options.Mode,
                    KillWithHost = cur.Options.KillWithHost,
                    SingleInstance = cur.Options.SingleInstance,
                    Restart = cur.Options.Restart,
                    RestartDelaySec = cur.Options.RestartDelaySec,
                    RestartLimit = cur.Options.RestartLimit,
                    StableUptimeSec = cur.Options.StableUptimeSec
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
            if (MessageBox.Show(Loc.T("Tasks.DeleteConfirm", "删除任务 \"{0}\" ？", cur.Name), Loc.T("Common.Confirm", "确认"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _tasks.Remove(cur);
            RefreshList();
        }

        private void OnBrowseFileClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog();
                dlg.Filter = Loc.T("Tasks.ScriptFilter", "脚本/可执行|*.ps1;*.js;*.py;*.bat;*.cmd;*.vbs;*.exe|所有文件|*.*");
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
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                string scripts = Services.ConfigService.ScriptsDirPath;
                string initial = Directory.Exists(scripts) ? scripts : null;
                if (CarroDesk.Common.FolderPicker.PickFolder(hwnd, Loc.T("Tasks.SelectWorkDir", "选择工作目录"), initial, out string path))
                {
                    WorkDirBox.Text = path;
                }
            }
            catch { }
        }

        private void OnWatchBrowseClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (CarroDesk.Common.FolderPicker.PickFolder(hwnd, Loc.T("Tasks.SelectWatchDir", "选择监听目录"), null, out string path))
                {
                    WatchPathBox.Text = path;
                }
            }
            catch { }
        }

        private TaskDefinition BuildCurrent(TaskDefinition cur = null)
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
            var modeSel = RunModeBox.SelectedItem as ComboBoxItem;
            t.Options.Mode = modeSel != null && modeSel.Tag != null ? modeSel.Tag.ToString() : "wait";
            t.Options.KillWithHost = KillWithHostBox.IsChecked == true;
            t.Options.Restart = RestartBox.IsChecked == true ? "on-failure" : "none";
            int rd, rl;
            if (int.TryParse(RestartDelaySecBox.Text.Trim(), out rd)) t.Options.RestartDelaySec = rd;
            if (int.TryParse(RestartLimitBox.Text.Trim(), out rl)) t.Options.RestartLimit = rl;
            int to, rt;
            if (int.TryParse(TimeoutBox.Text.Trim(), out to)) t.Options.TimeoutSec = to;
            if (int.TryParse(RetryBox.Text.Trim(), out rt)) t.Options.Retry = rt;
            t.When.OnlyIdle = OnlyIdleBox.IsChecked == true;
            t.When.AcPower = AcPowerBox.IsChecked == true;
            t.When.NetworkAvailable = NetworkBox.IsChecked == true;
            t.When.FileExists = FileExistsBox.Text.Trim();
            t.When.FileNotExists = FileNotExistsBox.Text.Trim();

            // UI 未暴露的选项字段（singleInstance / stableUptimeSec）在编辑既有任务时保留原值，
            // 否则保存时会被 new TaskOptions() 的默认值静默覆盖，导致磁盘配置丢失。
            if (cur != null && cur.Options != null)
            {
                t.Options.SingleInstance = cur.Options.SingleInstance;
                t.Options.StableUptimeSec = cur.Options.StableUptimeSec;
            }
            return t;
        }

        private TaskTriggerType ParseType(string tag)
        {
            TaskTriggerType type;
            return TaskConfigService.TryParseTriggerType(tag, out type) ? type : TaskTriggerType.Unknown;
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
                error = Loc.T("Tasks.ValNameDuplicate", "任务名称 \"{0}\" 已存在，请更换名称", built.Name);
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

            if (built.Trigger.Type == TaskTriggerType.Startup &&
                (built.Trigger.DelaySec < 0 || built.Trigger.DelaySec > 365 * 24 * 60 * 60))
            {
                error = Loc.T("Tasks.ValStartupDelay", "启动延迟必须在 0 到 31536000 秒之间");
                controlToFocus = DelayBox;
                return false;
            }
            else if (built.Trigger.Type == TaskTriggerType.Interval)
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
                    error = Loc.T("Tasks.ValDailyInvalid", "每天定时时间格式无效: {0} (请使用 HH:mm，如 09:30)", built.Trigger.At);
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
                    error = Loc.T("Tasks.ValCronInvalid", "Cron 表达式无效: {0}", cronErr);
                    controlToFocus = CronBox;
                    return false;
                }
                // 语法合法但永不可达（如 0 0 30 2 *）：存下去等于建一个永不执行的任务，
                // 而用户在列表里看不出任何异常。放在此处拦截而不是加载校验，
                // 是为了不影响既有配置里已存在的同类表达式（它们仍按"不触发"处理）。
                if (!CronHelper.GetNextOccurrence(built.Trigger.Expr, DateTime.Now).HasValue)
                {
                    error = Loc.T("Tasks.ValCronUnreachable",
                        "Cron 表达式语法合法但没有触发点（如 2 月 30 日），该任务永远不会执行");
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
                    error = Loc.T("Tasks.ValHotkeyInvalid", "热键格式无效: {0}", hkErr);
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

            // detach（常驻）组合校验：与模型层 TaskDefinition.Validate 保持一致，编辑器先给出友好提示
            if (built.Options != null && built.Options.IsDetach)
            {
                if (built.Options.TimeoutSec > 0)
                {
                    error = Loc.T("Tasks.ValDetachTimeout", "后台常驻模式不能设置超时（常驻进程不会被定时终止）");
                    controlToFocus = TimeoutBox;
                    return false;
                }
                if (built.Options.Retry > 0)
                {
                    error = Loc.T("Tasks.ValDetachRetry", "后台常驻模式不能设置失败重试（没有退出码可供重试判定）");
                    controlToFocus = RetryBox;
                    return false;
                }
                if (built.Options.AllowConcurrent)
                {
                    error = Loc.T("Tasks.ValDetachConcurrent", "后台常驻模式不能与\"允许并发\"同用（避免重复拉起多个常驻实例）");
                    controlToFocus = AllowConcurrentBox;
                    return false;
                }
                if (built.Options.RestartOnFailure)
                {
                    if (built.Options.RestartDelaySec < 1 || built.Options.RestartDelaySec > 3600)
                    {
                        error = Loc.T("Tasks.ValRestartDelayRange", "重启间隔必须在 1 到 3600 秒之间");
                        controlToFocus = RestartDelaySecBox;
                        return false;
                    }
                    if (built.Options.RestartLimit < 1 || built.Options.RestartLimit > 100)
                    {
                        error = Loc.T("Tasks.ValRestartLimitRange", "最大重启次数必须在 1 到 100 之间");
                        controlToFocus = RestartLimitBox;
                        return false;
                    }
                }
            }

            return true;
        }

        private void RunModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateRunModeUi();
        }

        private void UpdateRunModeUi()
        {
            var sel = RunModeBox.SelectedItem as ComboBoxItem;
            bool detach = sel != null && sel.Tag != null &&
                string.Equals(sel.Tag.ToString(), "detach", StringComparison.OrdinalIgnoreCase);
            KillWithHostBox.Visibility = detach ? Visibility.Visible : Visibility.Collapsed;
            PanelRestart.Visibility = detach ? Visibility.Visible : Visibility.Collapsed;
            RunModeHint.Visibility = detach ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SelectRunMode(string tag)
        {
            tag = (tag ?? "wait").ToLowerInvariant();
            for (int i = 0; i < RunModeBox.Items.Count; i++)
            {
                var it = RunModeBox.Items[i] as ComboBoxItem;
                if (it != null && it.Tag != null && it.Tag.ToString() == tag) { RunModeBox.SelectedIndex = i; return; }
            }
            RunModeBox.SelectedIndex = 0;
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
            var built = BuildCurrent(cur);
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

        /// <summary>
        /// 保存前的"加载错误"闸门。
        ///
        /// 加载失败的任务不会进入 _tasks，若直接保存会把它们从 tasks.json 里永久删除 ——
        /// 所以不能静默放行。但也不能一看到错误就硬拦：旧实现把 MessageBox 的 OK 按钮
        /// 当成了"取消"，用户手写错一个 cron 表达式后，打开编辑器点任何保存都被拦下，
        /// 唯一出路是手动改 JSON。这里改为明确告知代价并让用户自己选。
        /// </summary>
        private bool ConfirmProceedDespiteLoadErrors()
        {
            if (_loadErrors == null || _loadErrors.Count == 0) return true;

            string detail = Loc.T("Tasks.LoadErrors", "加载 tasks.json 有错误:\n{0}", string.Join("\n", _loadErrors));
            var answer = MessageBox.Show(
                detail + Environment.NewLine + Environment.NewLine +
                Loc.T("Tasks.LoadErrorsSaveConfirm",
                    "继续保存将把上述无效任务从 tasks.json 中移除（保存后可再次手工补回）。确定继续吗？"),
                Loc.T("Common.Prompt", "提示"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return answer == MessageBoxResult.Yes;
        }

        /// <summary>
        /// 保存成功后清空加载错误：磁盘上的 tasks.json 已被内存中的合法任务整体覆盖，
        /// 旧的解析错误不再成立。不清空的话，同一个过期告警会拦住本窗口后续的每一次保存。
        /// </summary>
        private void ClearLoadErrorsAfterSave()
        {
            if (_loadErrors == null || _loadErrors.Count == 0) return;
            foreach (var err in _loadErrors)
            {
                TaskLogger.Warn("", "save discarded invalid tasks.json entries: " + err);
            }
            _loadErrors = new List<string>();
        }

        private bool SaveTasksInternal()
        {
            if (!ConfirmProceedDespiteLoadErrors()) return false;

            var cur = TaskList.SelectedItem as TaskDefinition;
            var built = BuildCurrent(cur);

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
                MessageBox.Show(Loc.T("Tasks.CurrentTaskError", "当前任务表单存在错误，无法保存:\n{0}", err), Loc.T("Config.ValidationFailed", "校验失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
                if (seen.Contains(t.Name)) errors.Add(Loc.T("Tasks.DuplicatePrefix", "重名: ") + t.Name);
                else seen.Add(t.Name);
            }
            if (errors.Count > 0)
            {
                MessageBox.Show(Loc.T("Tasks.OtherTasksError", "其他任务存在错误，无法保存:\n{0}", string.Join("\n", errors)), Loc.T("Config.ValidationFailed", "校验失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // 3. 校验通过，先提交到内存列表；持久化失败时恢复原引用/移除新增项。
            TaskDefinition targetTask = cur;
            var previousTrigger = cur?.Trigger;
            var previousAction = cur?.Action;
            var previousOptions = cur?.Options;
            var previousWhen = cur?.When;
            string previousName = cur?.Name;
            bool previousEnabled = cur?.Enabled ?? false;
            bool addedNewTask = false;
            if (targetTask == null)
            {
                targetTask = built;
                _tasks.Add(targetTask);
                addedNewTask = true;
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
                ClearLoadErrorsAfterSave();
                ValidateText.Text = "✓ " + Loc.T("Tasks.SavedAt", "已保存 ({0})", DateTime.Now.ToString("HH:mm:ss"));

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
                if (addedNewTask) _tasks.Remove(targetTask);
                else if (targetTask != null)
                {
                    targetTask.Name = previousName;
                    targetTask.Enabled = previousEnabled;
                    targetTask.Trigger = previousTrigger;
                    targetTask.Action = previousAction;
                    targetTask.Options = previousOptions;
                    targetTask.When = previousWhen;
                }
                MessageBox.Show(Loc.T("Tasks.SaveFailed", "保存失败: {0}", ex.Message), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (SaveTasksInternal())
            {
                MessageBox.Show(Loc.T("Tasks.SaveSuccess", "已保存到 {0}", Services.ConfigService.TaskFilePath), Loc.T("Common.Success", "保存成功"), MessageBoxButton.OK, MessageBoxImage.Information);
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
                MessageBox.Show(Loc.T("Tasks.ReloadFailed", "重载失败: {0}", ex.Message), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                CronHintText.Text = Loc.T("Tasks.CronFormatError", "格式错误: {0}", err);
                CronHintText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x30, 0x25));
            }
            else
            {
                string desc = CronHelper.ExplainCron(expr);
                var next = CronHelper.GetNextOccurrence(expr, DateTime.Now);
                if (!next.HasValue)
                {
                    // 语法合法但永不可达（如 2 月 30 日）：绝不能显示成绿色的"✓ 有效"。
                    // 此前 nextStr 为空串、整行仍是绿色，用户看不出这个任务永远不会跑。
                    CronHintText.Text = Loc.T("Tasks.CronHintUnreachable",
                        "该表达式语法合法，但未来 8 年内没有任何触发点，任务不会执行");
                    CronHintText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x30, 0x25));
                }
                else
                {
                    CronHintText.Text = "✓ " + desc + Loc.T("Tasks.CronHintNext", next.Value);
                    CronHintText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x05, 0x96, 0x69));
                }
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
            // 防重入：运行中一律忽略（停止由 TestStopButton 负责）。
            // 右键菜单路径 sender 不是 Button，只靠按钮 IsEnabled 挡不住重复触发。
            if (_testRunActive) return;

            var selected = TaskList.SelectedItem as TaskDefinition;
            var cur = BuildCurrent();
            string err;
            Control focusCtrl;
            if (!ValidateForm(cur, selected, out err, out focusCtrl))
            {
                ValidateText.Text = Loc.T("Tasks.ValFailPrefix", "校验失败: ") + err;
                FocusInput(focusCtrl);
                MessageBox.Show(Loc.T("Tasks.TestError", "任务配置有误，无法测试运行:\n{0}", err), Loc.T("Config.ValidationFailed", "校验失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 任务自身未设超时的测试运行给 10s 兜底：被测程序常驻不退出时测试也能自动结束
            if (cur.Options != null && cur.Options.IsDetach)
            {
                // 常驻任务：启动即返回，不等待也无 10s 兜底；句柄由编辑器持有，不进调度器注册表
                StartTestRunDetached(cur);
                return;
            }

            if (cur.Options != null && cur.Options.TimeoutSec <= 0)
                cur.Options.TimeoutSec = TestRunFallbackTimeoutSec;

            _testRunActive = true;
            _testRunCts = new CancellationTokenSource();
            SetTestRunUi(running: true);
            ValidateText.Text = Loc.T("Tasks.TestRunning", "正在运行测试...");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var outcome = new TaskRunOutcome();
            try
            {
                int exitCode = await TaskRunner.RunAsync(cur, "manual-test", _testRunCts.Token, outcome).ConfigureAwait(true);
                sw.Stop();
                if (_testRunAbandoned) return; // 窗口已关闭，不再回弹状态与弹窗

                string status;
                var img = MessageBoxImage.Information;
                if (outcome.Cancelled)
                {
                    status = Loc.T("Tasks.TestRunCancelled", "已手动终止");
                    img = MessageBoxImage.Warning;
                }
                else if (outcome.TimedOut)
                {
                    status = Loc.T("Tasks.TestRunTimeout", "超时终止 (超过 {0} 秒未退出)",
                        cur.Options != null ? cur.Options.TimeoutSec : TestRunFallbackTimeoutSec);
                    img = MessageBoxImage.Warning;
                }
                else if (exitCode == 0)
                {
                    status = Loc.T("Common.Success", "成功");
                }
                else
                {
                    status = Loc.T("Tasks.FailedExitCode", exitCode);
                    img = MessageBoxImage.Warning;
                }
                ValidateText.Text = Loc.T("Tasks.TestCompletedSummary", "测试完成 [{0}] 耗时 {1:0.0}s", status, sw.Elapsed.TotalSeconds);
                MessageBox.Show(Loc.T("Tasks.TestResult", "测试运行完成: {0}\n耗时: {1:0.0} 秒\n详细日志请查看:\nlogs/task-{2}.log", status, sw.Elapsed.TotalSeconds, cur.Name), Loc.T("Tasks.TestResultTitle", "测试结果"), MessageBoxButton.OK, img);
            }
            catch (Exception ex)
            {
                if (_testRunAbandoned) return;
                ValidateText.Text = Loc.T("Tasks.TestException", "测试运行异常:\n{0}", ex.Message);
                MessageBox.Show(Loc.T("Tasks.TestException", "测试运行异常:\n{0}", ex.Message), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _testRunActive = false;
                if (_testRunCts != null) { try { _testRunCts.Dispose(); } catch { } _testRunCts = null; }
                SetTestRunUi(running: false);
            }
        }

        private void StartTestRunDetached(TaskDefinition cur)
        {
            _testRunActive = true;
            SetTestRunUi(running: true);
            ValidateText.Text = Loc.T("Tasks.TestStarting", "正在启动测试进程...");

            TaskProcessHandle handle;
            int code = TaskRunner.StartDetached(cur, "manual-test", out handle);
            if (code == TaskRunner.StartSkippedSingleInstance)
            {
                _testRunActive = false;
                SetTestRunUi(running: false);
                string skipMsg = Loc.T("Tasks.TestRunSingleInstanceSkip", "已有运行实例（单实例互斥），未启动测试进程");
                ValidateText.Text = skipMsg;
                MessageBox.Show(skipMsg, Loc.T("Tasks.TestResultTitle", "测试结果"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (code != 0 || handle == null)
            {
                _testRunActive = false;
                SetTestRunUi(running: false);
                string failMsg = Loc.T("Tasks.TestStartFailed", "启动失败，详见任务日志");
                ValidateText.Text = failMsg;
                MessageBox.Show(failMsg, Loc.T("Tasks.TestResultTitle", "测试结果"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _testRunHandle = handle;
            ValidateText.Text = Loc.T("Tasks.TestRunDetachedStatus", "已启动 pid={0}，后台运行中", handle.Pid);
            MessageBox.Show(
                Loc.T("Tasks.TestRunDetachedStarted", "测试进程已启动 pid={0}，在后台继续运行。\n可点\"停止测试\"或关闭本窗口终止它。", handle.Pid),
                Loc.T("Tasks.TestResultTitle", "测试结果"), MessageBoxButton.OK, MessageBoxImage.Information);

            handle.Exited += (h, exitCode) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_testRunHandle, h)) return;
                _testRunHandle = null;
                _testRunActive = false;
                SetTestRunUi(running: false);
                ValidateText.Text = Loc.T("Tasks.TestRunDetachedExited", "测试进程已退出 (pid={0}, exit={1})",
                    h.Pid, exitCode.HasValue ? exitCode.Value.ToString() : "?");
            }));
            handle.BeginExitWatch();
        }

        private void OnStopTestRunClick(object sender, RoutedEventArgs e)
        {
            var handle = _testRunHandle;
            if (handle != null)
            {
                ValidateText.Text = Loc.T("Tasks.TestRunStopping", "正在停止测试进程...");
                TestStopButton.IsEnabled = false;
                try { handle.Stop(); } catch { }
                return;
            }
            if (!_testRunActive) return;
            ValidateText.Text = Loc.T("Tasks.TestRunStopping", "正在停止测试进程...");
            TestStopButton.IsEnabled = false;
            var cts = _testRunCts;
            try { if (cts != null) cts.Cancel(); } catch { }
        }

        private void OnStopTaskClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var cur = TaskList.SelectedItem as TaskDefinition;
                string name = cur != null ? cur.Name : NameBox.Text.Trim();
                if (_scheduler == null || string.IsNullOrWhiteSpace(name)) return;
                if (_scheduler.TryStop(name)) ValidateText.Text = Loc.T("Tasks.StopTaskRequested", "已发送停止命令");
                else ValidateText.Text = Loc.T("Tasks.StopTaskNotRunning", "该任务当前没有运行中的实例");
            }
            catch (Exception ex)
            {
                ValidateText.Text = Loc.T("Tasks.StopTaskFailed", "停止失败: {0}", ex.Message);
            }
        }

        // 底栏运行状态轮询：选中任务的运行实例（pid/时长）与停止按钮可见性。
        // 用 1s DispatcherTimer 而非事件推送——调度器与编辑器的任务对象可能不同实例，按名查询最可靠。
        private System.Windows.Threading.DispatcherTimer _runStatusTimer;

        private void StartRunStatusTimer()
        {
            if (_runStatusTimer != null) return;
            _runStatusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _runStatusTimer.Tick += (s, e) => RefreshRunStatus();
            _runStatusTimer.Start();
        }

        private void RefreshRunStatus()
        {
            try
            {
                var cur = TaskList.SelectedItem as TaskDefinition;
                string name = cur != null ? cur.Name : NameBox.Text.Trim();
                List<TaskRunInfo> runs = new List<TaskRunInfo>();
                if (_scheduler != null && !string.IsNullOrWhiteSpace(name))
                {
                    foreach (var r in _scheduler.GetRunning())
                    {
                        if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) runs.Add(r);
                    }
                }
                var run = runs.Count > 0 ? runs[runs.Count - 1] : null;
                if (run != null)
                {
                    // 允许并发时同名任务可能有多条实例，全部列出才不会被"只看到最后一个"误导
                    RunStatusText.Text = runs.Count > 1
                        ? Loc.T("Tasks.RunStatusRunningMulti", "运行中 {0} 个实例，最新 pid={1} ({2:0}s)", runs.Count, run.Pid, run.DurationSec)
                        : Loc.T("Tasks.RunStatusRunning", "运行中 pid={0} ({1:0}s)", run.Pid, run.DurationSec);
                    StopTaskButton.Visibility = Visibility.Visible;
                }
                else
                {
                    // 未运行：检查熔断标记（守护连续失败超预算），提示复位途径
                    var sup = _scheduler != null && !string.IsNullOrWhiteSpace(name) ? _scheduler.GetSupervision(name) : null;
                    if (sup != null && sup.MarkedFailed)
                    {
                        RunStatusText.Text = Loc.T("Tasks.RunStatusMarked",
                            "已标记失败（连续失败 {0} 次）——保存任务或手动运行可重置", sup.ConsecutiveFailures);
                    }
                    else
                    {
                        RunStatusText.Text = "";
                    }
                    StopTaskButton.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        private void SetTestRunUi(bool running)
        {
            TestRunButton.IsEnabled = !running;
            TestStopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            TestStopButton.IsEnabled = true;
            // 右键菜单在 Window.Resources 中，x:Name 不生成代码字段，按位置取第一项（测试运行）
            var menu = TryFindResource("TaskItemContextMenu") as ContextMenu;
            var testRunItem = menu != null && menu.Items.Count > 0 ? menu.Items[0] as MenuItem : null;
            if (testRunItem != null) testRunItem.IsEnabled = !running;
        }

        private void OnItemEnabledToggleClick(object sender, RoutedEventArgs e)
        {
            if (!ConfirmProceedDespiteLoadErrors()) return;
            if (sender is CheckBox cb && cb.DataContext is TaskDefinition task)
            {
                bool previousEnabled = !(cb.IsChecked == true);
                try
                {
                    TaskConfigService.Save(_tasks);
                    ClearLoadErrorsAfterSave();
                    _scheduler?.Reload();
                    _onReloadCompleted?.Invoke();
                    if (TaskList.SelectedItem == task)
                    {
                        EnabledBox.IsChecked = task.Enabled;
                    }
                }
                catch (Exception ex)
                {
                    task.Enabled = previousEnabled;
                    MessageBox.Show(Loc.T("Tasks.SaveStatusFailed", "保存任务状态失败: {0}", ex.Message), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void OnMenuTestRunClick(object sender, RoutedEventArgs e)
        {
            OnTestRunClick(sender, e);
        }

        private void OnMenuViewLogClick(object sender, RoutedEventArgs e)
        {
            OnViewCurrentLogClick(sender, e);
        }

        private void OnOpenScriptsDirClick(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = Services.ConfigService.ScriptsDirPath;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Tasks.OpenScriptsDirFailed", "打开脚本目录失败: {0}", ex.Message), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnViewCurrentLogClick(object sender, RoutedEventArgs e)
        {
            var cur = TaskList.SelectedItem as TaskDefinition;
            string taskName = cur?.Name ?? NameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(taskName))
            {
                MessageBox.Show(Loc.T("Tasks.SelectTaskFirst", "请先选择一个任务"), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string logPath = TaskLogger.GetTaskLogPath(taskName);
            if (!File.Exists(logPath))
            {
                string aggLog = TaskLogger.GetAggregateLogPath();
                if (File.Exists(aggLog))
                {
                    OpenLogFile(aggLog);
                    return;
                }
                MessageBox.Show(Loc.T("Tasks.NoLogFile", "该任务尚未生成运行日志文件：\n{0}", logPath), Loc.T("Common.Prompt", "提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            OpenLogFile(logPath);
        }

        private void OpenLogFile(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Tasks.OpenLogFailed", "打开日志失败: {0}", ex.Message), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnOpenLogDirClick(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = Services.ConfigService.LogsDirPath;
                TaskLogger.EnsureLogDir();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Tasks.OpenLogsDirFailed", "打开日志目录失败: {0}", ex.Message), Loc.T("Common.Error", "错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 关窗时若有测试运行未结束：wait 取消令牌、detach 停止句柄——测试进程树都被终止，结果不再回弹 UI
            if (_testRunActive)
            {
                _testRunAbandoned = true;
                var cts = _testRunCts;
                try { if (cts != null) cts.Cancel(); } catch { }
                var handle = _testRunHandle;
                try { if (handle != null) handle.Stop(); } catch { }
            }
            base.OnClosing(e);
        }
    }
}
