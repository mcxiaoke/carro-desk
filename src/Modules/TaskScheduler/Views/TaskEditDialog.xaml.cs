using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CarroDesk.Common;
using CarroDesk.Models;
using CarroDesk.Services;
using CarroDesk.Services.Localization;
using CarroDesk.Services.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace CarroDesk.Modules.TaskScheduler.Views
{
    public partial class TaskEditDialog : Window
    {
        private readonly bool _isNew;
        private readonly HashSet<string> _existingNames;
        private bool _isDirty;
        private bool _isLoading = true;
        private CancellationTokenSource _testCts;

        public TaskDefinition ResultTask { get; private set; }

        public TaskEditDialog(TaskDefinition taskToEdit, bool isNew, IEnumerable<string> existingNames)
        {
            InitializeComponent();
            _isNew = isNew;
            _existingNames = new HashSet<string>(existingNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            if (_isNew)
            {
                Title = "新建自动化任务 - CarroDesk";
                HeaderTitleText.Text = "新建自动化任务";
            }
            else
            {
                string originalName = taskToEdit?.Name ?? "";
                Title = $"编辑任务: {originalName} - CarroDesk";
                HeaderTitleText.Text = $"编辑任务: {originalName}";
                _existingNames.Remove(originalName);
            }

            LoadScriptsDropdown();

            // 深拷贝一份以防直接污染外部
            var workingTask = taskToEdit != null
                ? JsonConvert.DeserializeObject<TaskDefinition>(JsonConvert.SerializeObject(taskToEdit))
                : new TaskDefinition { Name = "NewTask", Trigger = new TaskTrigger { Type = TaskTriggerType.Startup } };

            PopulateForm(workingTask);
            _isLoading = false;
            UpdateDirtyState(false);
        }

        private void LoadScriptsDropdown()
        {
            try
            {
                ScriptQuickBox.Items.Clear();
                ScriptQuickBox.Items.Add(new ComboBoxItem { Content = "-- scripts/ 脚本 --", Tag = "" });
                string dir = ConfigService.ScriptsDirPath;
                if (Directory.Exists(dir))
                {
                    var files = Directory.GetFiles(dir);
                    foreach (var f in files.OrderBy(Path.GetFileName))
                    {
                        string name = Path.GetFileName(f);
                        ScriptQuickBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });
                    }
                }
                ScriptQuickBox.SelectedIndex = 0;
            }
            catch { }
        }

        private void PopulateForm(TaskDefinition task)
        {
            if (task == null) return;

            NameBox.Text = task.Name ?? "";
            EnabledBox.IsChecked = task.Enabled;

            // Trigger
            var trig = task.Trigger ?? new TaskTrigger();
            string trigType = (trig.Type != TaskTriggerType.Unknown ? trig.Type.ToString() : trig.RawType).ToLowerInvariant();
            SelectComboByTag(TriggerTypeBox, trigType);

            DelaySecBox.Text = trig.DelaySec > 0 ? trig.DelaySec.ToString() : "5";
            IntervalBox.Text = !string.IsNullOrEmpty(trig.Every) ? trig.Every : (trig.EverySec > 0 ? trig.EverySec + "s" : "60s");
            DailyTimeBox.Text = !string.IsNullOrEmpty(trig.At) ? trig.At : "02:00";
            CronBox.Text = !string.IsNullOrEmpty(trig.Expr) ? trig.Expr : "0 9 * * 1";
            WatchPathBox.Text = trig.WatchPath ?? "";
            WatchFilterBox.Text = !string.IsNullOrEmpty(trig.WatchFilter) ? trig.WatchFilter : "*.*";
            SelectComboByTag(WatchEventBox, (trig.WatchEvent ?? "created").ToLowerInvariant());
            HotkeyBox.Text = trig.Hotkey ?? "";
            IdleMinutesBox.Text = trig.AfterMinutes > 0 ? trig.AfterMinutes.ToString() : "5";

            // Action
            var act = task.Action ?? new TaskAction();
            FileBox.Text = act.File ?? "";
            ArgsBox.Text = act.Args ?? "";
            WorkDirBox.Text = act.WorkDir ?? "";
            if (act.Env != null && act.Env.Count > 0)
            {
                EnvBox.Text = string.Join(Environment.NewLine, act.Env.Select(kv => $"{kv.Key}={kv.Value}"));
            }
            else
            {
                EnvBox.Text = "";
            }

            // Options
            var opt = task.Options ?? new TaskOptions();
            HiddenBox.IsChecked = opt.Hidden;
            RunAtStartupBox.IsChecked = opt.RunAtStartup;
            CatchUpMissedBox.IsChecked = opt.CatchUpMissed;
            NotifyOnFailureBox.IsChecked = opt.NotifyOnFailure;
            SelectComboByTag(ModeBox, (opt.Mode ?? "wait").ToLowerInvariant());
            SelectComboByTag(EncodingBox, (opt.Encoding ?? "").ToLowerInvariant());

            TimeoutBox.Text = opt.TimeoutSec.ToString();
            RetryBox.Text = opt.Retry.ToString();
            AllowConcurrentBox.IsChecked = opt.AllowConcurrent;

            KillWithHostBox.IsChecked = opt.KillWithHost;
            SingleInstanceBox.IsChecked = opt.SingleInstance;
            RestartOnFailureBox.IsChecked = opt.RestartOnFailure;
            RestartDelayBox.Text = opt.RestartDelaySec > 0 ? opt.RestartDelaySec.ToString() : "5";
            RestartLimitBox.Text = opt.RestartLimit > 0 ? opt.RestartLimit.ToString() : "3";

            // When
            var when = task.When ?? new TaskCondition();
            OnlyIdleBox.IsChecked = when.OnlyIdle;
            AcPowerBox.IsChecked = when.AcPower;
            NetworkBox.IsChecked = when.NetworkAvailable;
            FileExistsBox.Text = when.FileExists ?? "";
            FileNotExistsBox.Text = when.FileNotExists ?? "";

            UpdateTriggerPanels();
            UpdateModePanels();
            UpdateCronPreview();
        }

        private void SelectComboByTag(ComboBox combo, string tag)
        {
            if (combo == null) return;
            foreach (var item in combo.Items)
            {
                if (item is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = cbi;
                    return;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private string GetSelectedComboTag(ComboBox combo)
        {
            return (combo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        }

        private void OnFormFieldChanged(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            UpdateDirtyState(true);
        }

        private void UpdateDirtyState(bool dirty)
        {
            _isDirty = dirty;
            if (DirtyIndicator != null)
                DirtyIndicator.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TriggerTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateTriggerPanels();
            if (!_isLoading) UpdateDirtyState(true);
        }

        private void UpdateTriggerPanels()
        {
            string t = GetSelectedComboTag(TriggerTypeBox).ToLowerInvariant();
            if (PanelStartup == null) return;

            PanelStartup.Visibility = t == "startup" ? Visibility.Visible : Visibility.Collapsed;
            PanelInterval.Visibility = t == "interval" ? Visibility.Visible : Visibility.Collapsed;
            PanelDaily.Visibility = t == "daily" ? Visibility.Visible : Visibility.Collapsed;
            PanelCron.Visibility = t == "cron" ? Visibility.Visible : Visibility.Collapsed;
            PanelWatch.Visibility = t == "watch" ? Visibility.Visible : Visibility.Collapsed;
            PanelHotkey.Visibility = t == "hotkey" ? Visibility.Visible : Visibility.Collapsed;
            PanelIdle.Visibility = t == "idle" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnModeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateModePanels();
            if (!_isLoading) UpdateDirtyState(true);
        }

        private void UpdateModePanels()
        {
            string m = GetSelectedComboTag(ModeBox).ToLowerInvariant();
            bool isDetach = m == "detach";
            if (PanelWaitOptions == null || PanelDetachOptions == null) return;

            PanelWaitOptions.Visibility = isDetach ? Visibility.Collapsed : Visibility.Visible;
            PanelDetachOptions.Visibility = isDetach ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CronBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateCronPreview();
            if (!_isLoading) UpdateDirtyState(true);
        }

        private void UpdateCronPreview()
        {
            if (CronNextTimeText == null) return;
            string expr = CronBox?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(expr))
            {
                CronNextTimeText.Text = "下次: --";
                return;
            }
            try
            {
                var next = CronHelper.GetNextOccurrence(expr, DateTime.Now);
                if (next.HasValue)
                {
                    CronNextTimeText.Text = $"下次运行时间: {next.Value:yyyy-MM-dd HH:mm:ss}";
                    CronNextTimeText.Foreground = System.Windows.Media.Brushes.DarkGreen;
                }
                else
                {
                    CronNextTimeText.Text = "未来无触发时间点 (无法触发)";
                    CronNextTimeText.Foreground = System.Windows.Media.Brushes.Red;
                }
            }
            catch (Exception ex)
            {
                CronNextTimeText.Text = $"表达式语法错误: {ex.Message}";
                CronNextTimeText.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        private void OnBrowseFileClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择可执行程序或脚本文件",
                Filter = "常见程序与脚本 (*.exe;*.bat;*.cmd;*.ps1;*.vbs;*.js;*.py)|*.exe;*.bat;*.cmd;*.ps1;*.vbs;*.js;*.py|所有文件 (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                FileBox.Text = dlg.FileName;
                if (string.IsNullOrWhiteSpace(WorkDirBox.Text))
                {
                    WorkDirBox.Text = Path.GetDirectoryName(dlg.FileName) ?? "";
                }
            }
        }

        private void ScriptQuickBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            string script = GetSelectedComboTag(ScriptQuickBox);
            if (!string.IsNullOrEmpty(script))
            {
                FileBox.Text = script;
                if (string.IsNullOrWhiteSpace(WorkDirBox.Text))
                {
                    WorkDirBox.Text = ConfigService.ScriptsDirPath;
                }
            }
        }

        private void OnBrowseWorkDirClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "选择工作目录"
            };
            if (dlg.ShowDialog() == true)
            {
                WorkDirBox.Text = dlg.FolderName;
            }
        }

        private void OnBrowseWatchDirClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "选择监听目录"
            };
            if (dlg.ShowDialog() == true)
            {
                WatchPathBox.Text = dlg.FolderName;
            }
        }

        private void OnInsertTemplateClick(object sender, RoutedEventArgs e)
        {
            var cm = new ContextMenu();
            AddTemplateMenuItem(cm, "{{date}}", "当前日期 (yyyy-MM-dd)");
            AddTemplateMenuItem(cm, "{{time}}", "当前时间 (HH-mm-ss)");
            AddTemplateMenuItem(cm, "{{datetime}}", "完整时间戳 (yyyy-MM-dd_HH-mm-ss)");
            AddTemplateMenuItem(cm, "{{timestamp}}", "Unix 纪元秒数");
            AddTemplateMenuItem(cm, "{{task}}", "当前任务名称");
            cm.Items.Add(new Separator());
            AddTemplateMenuItem(cm, "{{file}}", "变动文件全路径 (watch专用)");
            AddTemplateMenuItem(cm, "{{fileName}}", "变动文件名 (watch专用)");
            AddTemplateMenuItem(cm, "{{fileDir}}", "变动文件所在目录 (watch专用)");
            AddTemplateMenuItem(cm, "{{fileEvent}}", "触发事件类型 (created/changed等)");
            cm.PlacementTarget = InsertTemplateBtn;
            cm.IsOpen = true;
        }

        private void AddTemplateMenuItem(ContextMenu menu, string token, string desc)
        {
            var mi = new MenuItem { Header = $"{token} - {desc}" };
            mi.Click += (s, ev) =>
            {
                ArgsBox.SelectedText = token;
                ArgsBox.Focus();
            };
            menu.Items.Add(mi);
        }

        private async void OnTestRunClick(object sender, RoutedEventArgs e)
        {
            var task = BuildTaskFromForm();
            if (task == null) return;

            var valError = task.Validate();
            if (!string.IsNullOrEmpty(valError))
            {
                MessageBox.Show(this, "任务配置校验未通过:\n" + valError, "无法测试", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TestRunButton.Visibility = Visibility.Collapsed;
            TestStopButton.Visibility = Visibility.Visible;
            TestStatusText.Text = "⏳ 正在启动测试进程...";
            _testCts = new CancellationTokenSource();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (task.Options.IsDetach)
                {
                    TaskProcessHandle handle;
                    int code = TaskRunner.StartDetached(task, "test-run", out handle);
                    if (code == TaskRunner.StartSkippedSingleInstance)
                    {
                        TestStatusText.Text = "单实例互斥：已有实例在运行，测试跳过";
                    }
                    else if (code == 0 && handle != null)
                    {
                        TestStatusText.Text = $"🟢 常驻测试已启动 PID: {handle.Pid}";
                    }
                    else
                    {
                        TestStatusText.Text = "🔴 启动失败，详见日志";
                    }
                }
                else
                {
                    int code = await TaskRunner.RunAsync(task, "test-run", _testCts.Token).ConfigureAwait(true);
                    sw.Stop();
                    if (_testCts.IsCancellationRequested)
                    {
                        TestStatusText.Text = "🛑 已手动终止";
                    }
                    else
                    {
                        TestStatusText.Text = code == 0
                            ? $"🟢 执行成功 (0) 耗时: {sw.Elapsed.TotalSeconds:0.0}s"
                            : $"🔴 执行返回 {code} 耗时: {sw.Elapsed.TotalSeconds:0.0}s";
                    }
                }
            }
            catch (Exception ex)
            {
                TestStatusText.Text = $"🔴 测试异常: {ex.Message}";
            }
            finally
            {
                TestRunButton.Visibility = Visibility.Visible;
                TestStopButton.Visibility = Visibility.Collapsed;
            }
        }

        private void OnTestStopClick(object sender, RoutedEventArgs e)
        {
            try { _testCts?.Cancel(); } catch { }
            TestStatusText.Text = "正在停止测试进程...";
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            var task = BuildTaskFromForm();
            if (task == null) return;

            string name = task.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(this, "请输入任务名称！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                NameBox.Focus();
                return;
            }

            if (_existingNames.Contains(name))
            {
                MessageBox.Show(this, $"任务名称 \"{name}\" 已存在，请更换！", "重名提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                NameBox.Focus();
                return;
            }

            var err = task.Validate();
            if (!string.IsNullOrEmpty(err))
            {
                MessageBox.Show(this, "任务配置校验未通过:\n" + err, "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultTask = task;
            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            if (_isDirty)
            {
                var res = MessageBox.Show(this, "当前有未保存的修改，确定放弃并退出吗？", "放弃确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes) return;
            }

            try { _testCts?.Cancel(); } catch { }
            DialogResult = false;
            Close();
        }

        private TaskDefinition BuildTaskFromForm()
        {
            var task = new TaskDefinition
            {
                Name = NameBox.Text.Trim(),
                Enabled = EnabledBox.IsChecked ?? true
            };

            // Trigger
            string trigType = GetSelectedComboTag(TriggerTypeBox);
            Enum.TryParse<TaskTriggerType>(trigType, true, out var ttype);
            task.Trigger = new TaskTrigger
            {
                Type = ttype,
                RawType = trigType,
                DelaySec = int.TryParse(DelaySecBox.Text.Trim(), out var delay) ? delay : 5,
                Every = IntervalBox.Text.Trim(),
                At = DailyTimeBox.Text.Trim(),
                Expr = CronBox.Text.Trim(),
                WatchPath = WatchPathBox.Text.Trim(),
                WatchFilter = WatchFilterBox.Text.Trim(),
                WatchEvent = GetSelectedComboTag(WatchEventBox),
                Hotkey = HotkeyBox.Text.Trim(),
                AfterMinutes = int.TryParse(IdleMinutesBox.Text.Trim(), out var idle) ? idle : 5
            };

            // Action
            var envDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(EnvBox.Text))
            {
                var lines = EnvBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        string k = line.Substring(0, eq).Trim();
                        string v = line.Substring(eq + 1).Trim();
                        if (!string.IsNullOrEmpty(k)) envDict[k] = v;
                    }
                }
            }

            task.Action = new TaskAction
            {
                File = FileBox.Text.Trim(),
                Args = ArgsBox.Text.Trim(),
                WorkDir = WorkDirBox.Text.Trim(),
                Env = envDict
            };

            // Options
            string mode = GetSelectedComboTag(ModeBox);
            bool isDetach = string.Equals(mode, "detach", StringComparison.OrdinalIgnoreCase);
            task.Options = new TaskOptions
            {
                Hidden = HiddenBox.IsChecked ?? true,
                RunAtStartup = RunAtStartupBox.IsChecked ?? false,
                CatchUpMissed = CatchUpMissedBox.IsChecked ?? false,
                NotifyOnFailure = NotifyOnFailureBox.IsChecked ?? true,
                Mode = mode,
                Encoding = GetSelectedComboTag(EncodingBox),
                TimeoutSec = !isDetach && int.TryParse(TimeoutBox.Text.Trim(), out var to) ? to : 0,
                Retry = !isDetach && int.TryParse(RetryBox.Text.Trim(), out var ret) ? ret : 0,
                AllowConcurrent = !isDetach && (AllowConcurrentBox.IsChecked ?? false),
                KillWithHost = isDetach && (KillWithHostBox.IsChecked ?? true),
                SingleInstance = isDetach && (SingleInstanceBox.IsChecked ?? false),
                Restart = isDetach && (RestartOnFailureBox.IsChecked ?? false) ? "on-failure" : "none",
                RestartDelaySec = int.TryParse(RestartDelayBox.Text.Trim(), out var rdelay) ? rdelay : 5,
                RestartLimit = int.TryParse(RestartLimitBox.Text.Trim(), out var rlimit) ? rlimit : 3
            };

            // When
            task.When = new TaskCondition
            {
                OnlyIdle = OnlyIdleBox.IsChecked ?? false,
                AcPower = AcPowerBox.IsChecked ?? false,
                NetworkAvailable = NetworkBox.IsChecked ?? false,
                FileExists = FileExistsBox.Text.Trim(),
                FileNotExists = FileNotExistsBox.Text.Trim()
            };

            return task;
        }
    }
}
