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
using Newtonsoft.Json.Linq;

namespace CarroDesk.Modules.TaskScheduler.Views
{
    public partial class TaskEditDialog : Window
    {
        private readonly bool _isNew;
        private readonly HashSet<string> _existingNames;
        private readonly TaskDefinition _sourceTask;
        private bool _isDirty;
        private bool _isLoading = true;
        private CancellationTokenSource _testCts;
        private TaskProcessHandle _testRunHandle;

        public TaskDefinition ResultTask { get; private set; }

        public TaskEditDialog(TaskDefinition taskToEdit, bool isNew, IEnumerable<string> existingNames)
        {
            InitializeComponent();
            _isNew = isNew;
            _existingNames = new HashSet<string>(existingNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            if (_isNew)
            {
                Title = Loc.T("Tasks.NewTitle", "新建自动化任务 - CarroDesk");
                HeaderTitleText.Text = "新建自动化任务";
            }
            else
            {
                string originalName = taskToEdit?.Name ?? "";
                Title = Loc.T("Tasks.EditTitle", "编辑任务: {0} - CarroDesk", originalName);
                HeaderTitleText.Text = $"编辑任务: {originalName}";
                _existingNames.Remove(originalName);
            }

            LoadScriptsDropdown();

            // 深拷贝一份以防直接污染外部；用 Clone() 而非 JsonConvert 往返，
            // 后者会丢掉 [JsonIgnore] 的 options 扩展字段（M11）
            var workingTask = taskToEdit != null
                ? taskToEdit.Clone()
                : new TaskDefinition { Name = "NewTask", Trigger = new TaskTrigger { Type = TaskTriggerType.Startup } };
            _sourceTask = workingTask;

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
            var optForDisplay = task.Options ?? new TaskOptions();
            FileBox.Text = act.File ?? "";
            ArgsBox.Text = act.Args ?? "";
            // EffectiveWorkDir 是 options.workDir 优先：显示当前"有效工作目录"，
            // 否则只配了 options.workDir 的任务在编辑器里显示为空、保存后目录丢失（H1）
            WorkDirBox.Text = !string.IsNullOrWhiteSpace(optForDisplay.WorkDir)
                ? optForDisplay.WorkDir
                : (act.WorkDir ?? "");
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
                Title = Loc.T("Tasks.SelectScriptFile", "选择可执行程序或脚本文件"),
                Filter = Loc.T("Tasks.FileDialogFilter", "常见程序与脚本 (*.exe;*.bat;*.cmd;*.ps1;*.vbs;*.js;*.py)|*.exe;*.bat;*.cmd;*.ps1;*.vbs;*.js;*.py|所有文件 (*.*)|*.*")
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
                Title = Loc.T("Tasks.SelectWorkDir")
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
                Title = Loc.T("Tasks.SelectWatchDir")
            };
            if (dlg.ShowDialog() == true)
            {
                WatchPathBox.Text = dlg.FolderName;
            }
        }

        private void OnInsertTemplateClick(object sender, RoutedEventArgs e)
        {
            var cm = new ContextMenu();
            AddTemplateMenuItem(cm, "{{date}}", Loc.T("Tasks.TplDate", "当前日期 (yyyy-MM-dd)"));
            AddTemplateMenuItem(cm, "{{time}}", Loc.T("Tasks.TplTime", "当前时间 (HH-mm-ss)"));
            AddTemplateMenuItem(cm, "{{datetime}}", Loc.T("Tasks.TplDateTime", "完整时间戳 (yyyy-MM-dd_HH-mm-ss)"));
            AddTemplateMenuItem(cm, "{{timestamp}}", Loc.T("Tasks.TplTimestamp", "Unix 纪元秒数"));
            AddTemplateMenuItem(cm, "{{task}}", Loc.T("Tasks.TplTask", "当前任务名称"));
            cm.Items.Add(new Separator());
            AddTemplateMenuItem(cm, "{{file}}", Loc.T("Tasks.TplFile", "变动文件全路径 (watch专用)"));
            AddTemplateMenuItem(cm, "{{fileName}}", Loc.T("Tasks.TplFileName", "变动文件名 (watch专用)"));
            AddTemplateMenuItem(cm, "{{fileDir}}", Loc.T("Tasks.TplFileDir", "变动文件所在目录 (watch专用)"));
            AddTemplateMenuItem(cm, "{{fileEvent}}", Loc.T("Tasks.TplFileEvent", "触发事件类型 (created/changed等)"));
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
            // detach 测试进程仍在跑时禁止再次测试：旧实现会直接覆盖 _testRunHandle，
            // 之前的常驻进程从此没有引用，"停止"和关窗兜底都只作用于最后一个。
            if (_testRunHandle != null && _testRunHandle.HasExited != true) return;

            if (!TryValidateNumericInputs(out string inputError))
            {
                MessageBox.Show(this, inputError, Loc.T("Tasks.ValFailTitle", "校验失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var task = BuildTaskFromForm();
            if (task == null) return;

            var valError = task.Validate();
            if (!string.IsNullOrEmpty(valError))
            {
                MessageBox.Show(this, Loc.Format("Tasks.TestError", valError), Loc.T("Tasks.TestInvalidTitle", "无法测试"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DisposeTestCts();   // 兜底清理上一次遗留
            var cts = new CancellationTokenSource();
            _testCts = cts;
            bool detachStarted = false;

            TestRunButton.Visibility = Visibility.Collapsed;
            TestStopButton.Visibility = Visibility.Visible;
            TestStatusText.Text = Loc.T("Tasks.TestStarting");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (task.Options.IsDetach)
                {
                    TaskProcessHandle handle;
                    int code = TaskRunner.StartDetached(task, "test-run", out handle);
                    if (code == TaskRunner.StartSkippedSingleInstance)
                    {
                        TestStatusText.Text = Loc.T("Tasks.TestRunSingleInstanceSkip");
                    }
                    else if (code == 0 && handle != null)
                    {
                        // 保存句柄供"停止"按钮与关窗清理使用；否则测试进程脱管，无法停止（M4）
                        _testRunHandle = handle;
                        detachStarted = true;   // 保持"停止"态，用户必须先停掉才能再测
                        TestStatusText.Text = Loc.Format("Tasks.TestRunDetachedStatus", handle.Pid);
                    }
                    else
                    {
                        TestStatusText.Text = Loc.T("Tasks.TestStartFailed");
                    }
                }
                else
                {
                    // 用局部变量而非 _testCts：窗口在 await 期间关闭时，OnClosing 会把字段置 null。
                    int code = await TaskRunner.RunAsync(task, "test-run", cts.Token).ConfigureAwait(true);
                    sw.Stop();
                    if (!IsLoaded) return;   // 已关窗，不再触碰控件
                    if (cts.IsCancellationRequested)
                    {
                        TestStatusText.Text = Loc.T("Tasks.TestRunCancelled");
                    }
                    else
                    {
                        TestStatusText.Text = code == 0
                            ? Loc.T("Tasks.TestRunSuccess", "🟢 执行成功 (0) 耗时: {0}s", sw.Elapsed.TotalSeconds.ToString("0.0"))
                            : Loc.T("Tasks.TestRunExitCode", "🔴 执行返回 {0} 耗时: {1}s", code, sw.Elapsed.TotalSeconds.ToString("0.0"));
                    }
                }
            }
            catch (Exception ex)
            {
                if (IsLoaded) TestStatusText.Text = Loc.Format("Tasks.TestException", ex.Message);
            }
            finally
            {
                if (!detachStarted)
                {
                    // wait 模式已结束：释放本次 CTS，避免下次点击直接 new 覆盖造成泄漏。
                    DisposeTestCts(cts);
                    RestoreTestButtons();
                }
            }
        }

        private void OnTestStopClick(object sender, RoutedEventArgs e)
        {
            try { _testCts?.Cancel(); } catch { }
            // detach 测试运行没有可取消的等待循环，直接终止进程（句柄在启动成功时保存）
            var handle = _testRunHandle;
            _testRunHandle = null;
            if (handle != null && handle.HasExited != true)
            {
                try { handle.Stop(); } catch { }
            }
            DisposeTestCts();
            if (IsLoaded) TestStatusText.Text = Loc.T("Tasks.TestRunStopping");
            RestoreTestButtons();
        }

        /// <summary>释放测试用 CTS；expected 用于只释放本次那一轮，避免误伤新测试。</summary>
        private void DisposeTestCts(CancellationTokenSource expected = null)
        {
            var cts = expected ?? _testCts;
            if (ReferenceEquals(_testCts, cts)) _testCts = null;
            if (cts == null) return;
            try { cts.Dispose(); } catch { }
        }

        private void RestoreTestButtons()
        {
            if (!IsLoaded) return;   // 窗口已关闭时不触碰控件
            TestRunButton.Visibility = Visibility.Visible;
            TestStopButton.Visibility = Visibility.Collapsed;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 右上角关闭 / Alt+F4 与"取消"按钮保持一致：有未保存修改时先询问（M17）
            if (_isDirty)
            {
                var res = MessageBox.Show(this,
                    Loc.T("Tasks.CloseUnsavedConfirm", "当前有未保存的修改，是否保存后关闭？"),
                    Loc.T("Tasks.CloseUnsavedTitle", "未保存的修改"),
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (res == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                if (res == MessageBoxResult.Yes)
                {
                    // OnClosing 内不能直接设置 DialogResult（窗口已在关闭流程中），
                    // 先取消本次关闭，回到消息循环后再执行保存并关闭。
                    e.Cancel = true;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!TrySaveTask()) return;   // 校验失败/重名：保持窗口打开
                        DialogResult = true;
                        Close();
                    }));
                    return;
                }
                // No：放弃修改，继续关闭
            }

            // 关窗兜底：detach 测试进程不能因对话框关闭而变成脱管常驻进程（M4）
            try
            {
                var handle = _testRunHandle;
                _testRunHandle = null;
                if (handle != null && handle.HasExited != true)
                {
                    try { handle.Stop(); } catch { }
                }
                try { _testCts?.Cancel(); } catch { }
                DisposeTestCts();
            }
            catch { }
            base.OnClosing(e);
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (!TrySaveTask()) return;
            DialogResult = true;
            Close();
        }

        /// <summary>校验数值输入并生成保存结果；失败时保持对话框打开并提示具体字段。</summary>
        private bool TrySaveTask()
        {
            if (!TryValidateNumericInputs(out string inputError))
            {
                MessageBox.Show(this, inputError, Loc.T("Tasks.ValFailTitle", "校验失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var task = BuildTaskFromForm();
            if (task == null) return false;

            string name = task.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(this, Loc.T("Tasks.ValNameRequired"), Loc.T("Common.Prompt"), MessageBoxButton.OK, MessageBoxImage.Warning);
                NameBox.Focus();
                return false;
            }

            if (_existingNames.Contains(name))
            {
                MessageBox.Show(this, Loc.Format("Tasks.ValNameDuplicate", name), Loc.T("Tasks.DuplicateTitle", "重名提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
                NameBox.Focus();
                return false;
            }

            var err = task.Validate();
            if (!string.IsNullOrEmpty(err))
            {
                MessageBox.Show(this, Loc.Format("Tasks.TestError", err), Loc.T("Tasks.ValFailTitle", "校验失败"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            ResultTask = task;
            UpdateDirtyState(false);   // 关闭流程会再次进入 OnClosing，不能重复追问
            return true;
        }

        /// <summary>
        /// 数字输入统一校验（M16）：此前 TryParse 失败会静默回退默认值，用户以为保存了自己的输入。
        /// 只校验当前可见/生效的字段，避免隐藏面板里的旧值挡住保存。
        /// </summary>
        private bool TryValidateNumericInputs(out string error)
        {
            string trigType = GetSelectedComboTag(TriggerTypeBox).ToLowerInvariant();
            if (trigType == "startup" &&
                !TryReadInt(DelaySecBox, 0, 365 * 24 * 60 * 60, Loc.T("Tasks.MgrDelaySeconds", "延迟秒数"), out error))
                return false;
            if (trigType == "idle" &&
                !TryReadInt(IdleMinutesBox, 1, 10080, Loc.T("Tasks.MgrIdleMinutes", "空闲分钟数"), out error))
                return false;

            bool isDetach = string.Equals(GetSelectedComboTag(ModeBox), "detach", StringComparison.OrdinalIgnoreCase);
            if (!isDetach)
            {
                if (!TryReadInt(TimeoutBox, 0, 7 * 24 * 60 * 60, Loc.T("Tasks.MgrTimeoutSeconds", "超时秒数"), out error)) return false;
                if (!TryReadInt(RetryBox, 0, 100, Loc.T("Tasks.MgrRetryOnFailure", "失败重试"), out error)) return false;
            }
            else if (RestartOnFailureBox.IsChecked == true)
            {
                if (!TryReadInt(RestartDelayBox, 1, 3600, Loc.T("Tasks.MgrRestartDelay", "重启延迟"), out error)) return false;
                if (!TryReadInt(RestartLimitBox, 1, 100, Loc.T("Tasks.MgrMaxFailureBudget", "最大失败预算"), out error)) return false;
            }

            error = null;
            return true;
        }

        private bool TryReadInt(TextBox box, int min, int max, string label, out string error)
        {
            string text = box?.Text?.Trim() ?? "";
            int value;
            if (!int.TryParse(text, out value) || value < min || value > max)
            {
                error = Loc.Format("Tasks.ValNumberRange", "{0} 必须是 {1} 到 {2} 之间的整数", label, min, max);
                box?.Focus();
                box?.SelectAll();
                return false;
            }
            error = null;
            return true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            if (_isDirty)
            {
                var res = MessageBox.Show(this, Loc.T("Tasks.DiscardConfirm", "当前有未保存的修改，确定放弃并退出吗？"), Loc.T("Tasks.DiscardTitle", "放弃确认"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes) return;
                UpdateDirtyState(false);   // 已确认放弃，关闭时不再重复追问
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
                // wait 模式也必须按复选框持久化：旧写法恒为 false，导致任务进程不挂 JobObject，
                // 宿主退出后继续存活却不再被跟踪，下次启动可拉起重复实例。
                KillWithHost = KillWithHostBox.IsChecked ?? true,
                SingleInstance = isDetach && (SingleInstanceBox.IsChecked ?? false),
                Restart = isDetach && (RestartOnFailureBox.IsChecked ?? false) ? "on-failure" : "none",
                RestartDelaySec = int.TryParse(RestartDelayBox.Text.Trim(), out var rdelay) ? rdelay : 5,
                RestartLimit = int.TryParse(RestartLimitBox.Text.Trim(), out var rlimit) ? rlimit : 3
            };
            // 编辑既有任务时保留 UI 未暴露的选项字段，否则会被上面的默认值静默覆盖（H1，同旧编辑器 TaskEditorWindow 的防线）：
            // stableUptimeSec（守护计数复位阈值，0=永不复位）没有对应控件，必须从源任务透传。
            if (_sourceTask?.Options != null)
            {
                task.Options.StableUptimeSec = _sourceTask.Options.StableUptimeSec;
                // 手写在 tasks.json 里的扩展字段同样透传，否则 GUI 保存一次即被删除（M11）
                task.Options.ExtraOptions = _sourceTask.Options.ExtraOptions != null
                    ? (JObject)_sourceTask.Options.ExtraOptions.DeepClone()
                    : null;
            }
            // 工作目录统一由 action.workDir 承载（表单只有一个输入框，显示的是原 options.workDir 优先的有效值）；
            // 清空 options.workDir，避免旧值继续以更高优先级压过用户在表单里改的新工作目录。
            task.Options.WorkDir = "";

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
