using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CarroDesk.Core;
using CarroDesk.Models;
using CarroDesk.Services;
using CarroDesk.Services.Localization;
using CarroDesk.Services.Tasks;

namespace CarroDesk.Modules.TaskScheduler.Views
{
    public partial class TaskManagerWindow : Window
    {
        private readonly ITaskSchedulerService _scheduler;
        private readonly Action _onTasksChanged;
        private readonly ObservableCollection<TaskRowViewModel> _items = new ObservableCollection<TaskRowViewModel>();
        private List<TaskDefinition> _tasks = new List<TaskDefinition>();
        /// <summary>本次加载的基线：记录磁盘原文与只读标记，保存前用于拒绝覆盖。</summary>
        private TaskLoadResult _loadResult;
        private readonly DispatcherTimer _ticker;
        private readonly DispatcherTimer _perfTicker;
        private readonly DispatcherTimer _searchDebounce;
        private int _perfSampling;   // 0/1：性能采样进行中（防止重叠）
        private volatile bool _closed;              // 窗口关闭后所有回投一律丢弃
        private CancellationTokenSource _sampleCts; // 当前飞行采样轮次，失活时取消
        private int _sampleGeneration;              // 采样代际：丢弃过期结果
        private int _pendingResample;               // 采样被单飞挡住时，标记需要立即补一轮

        public TaskManagerWindow(ITaskSchedulerService scheduler, Action onTasksChanged = null)
        {
            InitializeComponent();
            _scheduler = scheduler;
            _onTasksChanged = onTasksChanged;

            TaskListView.ItemsSource = _items;

            if (_scheduler != null)
            {
                GlobalEnabledBox.IsChecked = _scheduler.IsGlobalEnabled;
                _scheduler.StateChanged += OnSchedulerStateChanged;
            }

            // 1s 轻量心跳：仅就地刷新"已运行"列文案（纯字符串格式化，不读取任何进程性能数据）
            _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _ticker.Tick += (s, e) => RefreshLiveDurations();
            _ticker.Start();

            // 3s 性能采样（内存占用）：只有本窗口可见且未最小化时才读取进程工作集。
            // 这是性能类数据，后台无界面（最小化 / 窗口关闭）时必须零读取，避免无谓的句柄与 CPU 开销。
            _perfTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _perfTicker.Tick += (s, e) => SampleMemoryIfVisible();
            _perfTicker.Start();

            // 搜索逐字输入会整表重建，200ms 防抖后再刷新
            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _searchDebounce.Tick += (s, e) => { _searchDebounce.Stop(); ApplyFilterAndRefreshList(); };

            // 最小化即视为"无界面"：停掉心跳与采样；还原时恢复并立即补采一次
            StateChanged += (s, e) => UpdateSamplingActivity();
            IsVisibleChanged += (s, e) => UpdateSamplingActivity();

            Closed += (s, e) =>
            {
                _closed = true;
                _ticker.Stop();
                _perfTicker.Stop();
                _searchDebounce.Stop();
                CancelInFlightSampling();
                if (_scheduler != null)
                {
                    _scheduler.StateChanged -= OnSchedulerStateChanged;
                }
            };

            LoadTasks();
        }

        /// <summary>按窗口可见性启停心跳与性能采样定时器。</summary>
        private void UpdateSamplingActivity()
        {
            bool active = IsVisible && WindowState != WindowState.Minimized;
            if (active)
            {
                if (!_ticker.IsEnabled) _ticker.Start();
                if (!_perfTicker.IsEnabled) _perfTicker.Start();
                SampleMemoryIfVisible();
            }
            else
            {
                _ticker.Stop();
                _perfTicker.Stop();
                // 只停 Timer 不够：已经在 Task.Run 里飞行的那一轮仍会逐个打开进程句柄读工作集。
                CancelInFlightSampling();
            }
        }

        /// <summary>失活/关闭时取消飞行采样，保证"后台零读取"。</summary>
        private void CancelInFlightSampling()
        {
            var cts = _sampleCts;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }
            Volatile.Write(ref _pendingResample, 0);
            // 飞行任务若因取消直接 return（不再回投），这里必须复位，否则后续采样永远被单飞标志挡住。
            Volatile.Write(ref _perfSampling, 0);
        }

        private void RefreshLiveDurations()
        {
            if (WindowState == WindowState.Minimized) return;
            foreach (var item in _items)
            {
                if (item.IsActive) item.RefreshLiveDuration();
            }
        }

        /// <summary>
        /// 进程内存采样：仅在窗口可见时执行。
        /// 读取工作集需要打开进程句柄，属性能类数据，无界面时不应有任何读取。
        /// 采样放到线程池执行后回投 UI 线程，避免个别句柄（受保护进程 / 已退出）阻塞界面。
        /// </summary>
        private void SampleMemoryIfVisible()
        {
            if (_closed || !IsVisible || WindowState == WindowState.Minimized) return;
            if (Interlocked.Exchange(ref _perfSampling, 1) != 0)
            {
                // 状态跃迁 / 列表重建后的"立即补采"不能被静默吞掉：等飞行轮回投后再补一轮。
                Volatile.Write(ref _pendingResample, 1);
                return;
            }

            // 只快照任务名 + PID，不持有 ViewModel 引用：列表随时可能被搜索/状态刷新重建，
            // 旧实现会把结果写回已从集合移除的孤立对象。
            List<KeyValuePair<string, int>> targets = null;
            foreach (var item in _items)
            {
                if (!item.IsRunning || !item.Pid.HasValue) continue;
                if (targets == null) targets = new List<KeyValuePair<string, int>>(_items.Count);
                targets.Add(new KeyValuePair<string, int>(item.Name, item.Pid.Value));
            }

            if (targets == null)
            {
                foreach (var item in _items) item.SetMemoryBytes(null);
                Volatile.Write(ref _perfSampling, 0);
                return;
            }

            var cts = new CancellationTokenSource();
            _sampleCts = cts;
            int generation = Interlocked.Increment(ref _sampleGeneration);

            try
            {
                Task.Run(() =>
                {
                    var results = new List<KeyValuePair<string, long?>>(targets.Count);
                    foreach (var t in targets)
                    {
                        if (cts.IsCancellationRequested) break;   // 失活/关闭后不再打开进程句柄
                        results.Add(new KeyValuePair<string, long?>(t.Key, ReadWorkingSetBytes(t.Value)));
                    }
                    bool cancelled = cts.IsCancellationRequested;

                    // 无论是否被取消都要回投一次，保证 FinishSampling 一定执行并释放 CTS。
                    if (!TryPost(() =>
                    {
                        try
                        {
                            if (cancelled || _closed ||
                                Volatile.Read(ref _sampleGeneration) != generation) return;  // 过期结果丢弃
                            foreach (var r in results)
                            {
                                var current = _items.FirstOrDefault(i =>
                                    string.Equals(i.Name, r.Key, StringComparison.OrdinalIgnoreCase));
                                if (current != null) current.SetMemoryBytes(r.Value);
                            }
                        }
                        finally { FinishSampling(cts); }
                    }))
                    {
                        FinishSampling(cts);
                    }
                });
            }
            catch
            {
                FinishSampling(cts);
            }
        }

        /// <summary>所有 Dispatcher 回投统一过这道门：窗口关闭后不再触碰控件。</summary>
        private bool TryPost(Action action)
        {
            if (_closed) return false;
            try
            {
                Dispatcher.BeginInvoke(action);
                return true;
            }
            catch (Exception ex)
            {
                // Dispatcher 已 shutdown：只记日志，不把异常抛给后台任务（未观察异常）。
                TaskLogger.Warn("system", "task manager dispatcher post failed: " + ex.Message);
                return false;
            }
        }

        private void FinishSampling(CancellationTokenSource cts)
        {
            Volatile.Write(ref _perfSampling, 0);
            if (cts != null)
            {
                if (ReferenceEquals(_sampleCts, cts)) _sampleCts = null;
                try { cts.Dispose(); } catch { }
            }
            if (_closed || Interlocked.Exchange(ref _pendingResample, 0) != 1) return;
            // 补采必须回到 UI 线程（FinishSampling 也可能在后台线程被调用，不能直接读 IsVisible）。
            TryPost(SampleMemoryIfVisible);
        }

        /// <summary>
        /// 读取进程物理工作集（RAM），失败返回 null（进程已退出 / 权限不足 / pid 已被回收）。
        /// 注意：不要在这里先调用 Process.HasExited —— 该属性需要 SYNCHRONIZE 权限，
        /// 对部分受保护/受限进程会抛"拒绝访问"，把本来可读的工作集误判为不可读。
        /// </summary>
        private static long? ReadWorkingSetBytes(int pid)
        {
            if (pid <= 0) return null;
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    long bytes = p.WorkingSet64;
                    return bytes > 0 ? bytes : (long?)null;
                }
            }
            catch { return null; }
        }

        private void OnSchedulerStateChanged(string taskName, TaskRuntimeState newState, TaskStateSnapshot snapshot)
        {
            TryPost(() =>
            {
                if (_closed) return;
                var item = _items.FirstOrDefault(i => string.Equals(i.Name, taskName, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    item.UpdateSnapshot(snapshot);
                    UpdateToolbarButtons();
                    UpdateStatsSummary();
                    SampleMemoryIfVisible();   // 状态跃迁（启动完成 / 退出）后立即补采一次内存
                }
            });
        }

        public void LoadTasks()
        {
            TaskLoadResult result = null;
            try { result = TaskConfigService.Load(); }
            catch (Exception ex)
            {
                result = new TaskLoadResult
                {
                    ReadOnly = true,
                    ReadOnlyReason = "加载 tasks.json 异常（" + ex.Message + "），已只读打开，磁盘文件未改动"
                };
            }
            if (result == null)
            {
                result = new TaskLoadResult { ReadOnly = true, ReadOnlyReason = "加载任务配置失败，已只读打开" };
            }

            _loadResult = result;
            _tasks = result.Tasks ?? new List<TaskDefinition>();
            UpdateLoadIssueBanner(result);

            ApplyFilterAndRefreshList();
            UpdateStatsSummary();
            UpdateToolbarButtons();
        }

        /// <summary>
        /// 只在配置有问题时显示一条非模态横幅（不弹窗打扰）；正常加载时完全隐藏。
        /// </summary>
        private void UpdateLoadIssueBanner(TaskLoadResult result)
        {
            if (LoadIssueBanner == null) return;

            string reason = result?.ReadOnlyReason;
            if (string.IsNullOrEmpty(reason) && result != null && result.FileRecovered)
            {
                reason = "tasks.json 已损坏并备份重建：" + result.RecoveredBackupPath +
                         "（当前列表为空，请检查备份文件）";
            }

            if (string.IsNullOrEmpty(reason))
            {
                LoadIssueBanner.Visibility = Visibility.Collapsed;
                LoadIssueText.Text = "";
                return;
            }

            LoadIssueText.Text = reason;
            LoadIssueBanner.Visibility = Visibility.Visible;
        }

        private void ApplyFilterAndRefreshList()
        {
            string query = SearchBox?.Text?.Trim() ?? "";
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;

            var filtered = _tasks.AsEnumerable();
            if (!string.IsNullOrEmpty(query))
            {
                filtered = filtered.Where(t =>
                    (t.Name != null && t.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (t.Action?.File != null && t.Action.File.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (t.Action?.Args != null && t.Action.Args.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            var selectedName = GetSelectedTask()?.Name;

            _items.Clear();
            var states = _scheduler?.GetAllStates() ?? new Dictionary<string, TaskStateSnapshot>();

            foreach (var t in filtered)
            {
                states.TryGetValue(t.Name, out var snap);
                _items.Add(new TaskRowViewModel(t, snap));
            }

            EmptyHintPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (!string.IsNullOrEmpty(selectedName))
            {
                var sel = _items.FirstOrDefault(i => string.Equals(i.Name, selectedName, StringComparison.OrdinalIgnoreCase));
                if (sel != null) TaskListView.SelectedItem = sel;
            }

            SampleMemoryIfVisible();   // 列表重建后立即采样（窗口未显示时此调用不产生任何读取）
        }

        private void UpdateStatsSummary()
        {
            int total = _tasks.Count;
            int running = 0;
            int disabled = _tasks.Count(t => !t.Enabled);

            if (_scheduler != null)
            {
                var states = _scheduler.GetAllStates();
                running = states.Values.Count(s => s.IsRunning);
            }

            StatsSummaryText.Text = $"共 {total} 个任务 · {running} 个运行中 · {disabled} 个已禁用";
        }

        private TaskRowViewModel GetSelectedRow()
        {
            return TaskListView?.SelectedItem as TaskRowViewModel;
        }

        private TaskDefinition GetSelectedTask()
        {
            return GetSelectedRow()?.Task;
        }

        private void UpdateToolbarButtons()
        {
            var row = GetSelectedRow();
            bool hasSelection = row != null;
            bool isRunning = row != null && row.IsRunning;

            ToolbarRunBtn.IsEnabled = hasSelection;
            ToolbarStopBtn.IsEnabled = hasSelection && isRunning;
            ToolbarRestartBtn.IsEnabled = hasSelection;
            ToolbarEditBtn.IsEnabled = hasSelection;
            ToolbarLogBtn.IsEnabled = hasSelection;
            ToolbarDeleteBtn.IsEnabled = hasSelection;
        }

        private void TaskListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateToolbarButtons();
        }

        private void TaskListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (GetSelectedRow() != null)
            {
                OnEditClick(sender, e);
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 逐字输入立即整表重建会丢失选中/滚动位置并触发一次采样，改为 200ms 防抖。
            if (SearchPlaceholder != null)
            {
                SearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(SearchBox?.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            _scheduler?.Reload();
            LoadTasks();
        }

        private void OnGlobalEnabledClick(object sender, RoutedEventArgs e)
        {
            bool enabled = GlobalEnabledBox.IsChecked ?? true;
            _scheduler?.SetGlobalEnabled(enabled);
            _onTasksChanged?.Invoke();
            UpdateStatsSummary();
        }

        private void OnNewTaskClick(object sender, RoutedEventArgs e)
        {
            var existingNames = _tasks.Select(t => t.Name).ToList();
            var dlg = new TaskEditDialog(null, true, existingNames) { Owner = this };
            if (dlg.ShowDialog() == true && dlg.ResultTask != null)
            {
                _tasks.Add(dlg.ResultTask);
                SaveTasksAndNotify(dlg.ResultTask.Name);
            }
        }

        private void OnEditClick(object sender, RoutedEventArgs e)
        {
            var row = GetSelectedRow();
            if (row == null) return;

            var existingNames = _tasks.Select(t => t.Name).ToList();
            var dlg = new TaskEditDialog(row.Task, false, existingNames) { Owner = this };
            if (dlg.ShowDialog() == true && dlg.ResultTask != null)
            {
                int index = _tasks.FindIndex(t => string.Equals(t.Name, row.Task.Name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    _tasks[index] = dlg.ResultTask;
                    SaveTasksAndNotify(dlg.ResultTask.Name);
                }
            }
        }

        private void OnDuplicateClick(object sender, RoutedEventArgs e)
        {
            var row = GetSelectedRow();
            if (row == null) return;

            var clone = row.Task.Clone();
            clone.Name = FindUniqueName(clone.Name + "_copy");

            var existingNames = _tasks.Select(t => t.Name).ToList();
            var dlg = new TaskEditDialog(clone, true, existingNames) { Owner = this };
            if (dlg.ShowDialog() == true && dlg.ResultTask != null)
            {
                _tasks.Add(dlg.ResultTask);
                SaveTasksAndNotify(dlg.ResultTask.Name);
            }
        }

        private string FindUniqueName(string baseName)
        {
            string name = baseName;
            int counter = 1;
            while (_tasks.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                name = $"{baseName}_{counter++}";
            }
            return name;
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var row = GetSelectedRow();
            if (row == null) return;

            var res = MessageBox.Show(this, $"确定要删除任务 \"{row.Name}\" 吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;

            if (row.IsRunning)
            {
                _scheduler?.TryStop(row.Name);
            }

            _tasks.RemoveAll(t => string.Equals(t.Name, row.Name, StringComparison.OrdinalIgnoreCase));
            SaveTasksAndNotify(null);
        }

        private void OnToggleEnabledClick(object sender, RoutedEventArgs e)
        {
            var row = GetSelectedRow();
            if (row == null) return;

            row.Task.Enabled = !row.Task.Enabled;
            if (!row.Task.Enabled && row.IsRunning)
            {
                _scheduler?.TryStop(row.Name);
            }

            SaveTasksAndNotify(row.Name);
        }

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
            var row = GetSelectedRow();
            if (row == null) return;

            if (!_scheduler.IsGlobalEnabled)
            {
                MessageBox.Show(this, "任务调度总开关已禁用，请先启用总开关！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool ok = _scheduler.RunManual(row.Name);
            if (!ok)
            {
                MessageBox.Show(this, $"任务 \"{row.Name}\" 触发失败（可能已被禁用或不满足运行条件）。", "触发失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnStopClick(object sender, RoutedEventArgs e)
        {
            var row = GetSelectedRow();
            if (row == null) return;

            bool ok = _scheduler.TryStop(row.Name);
            if (!ok)
            {
                MessageBox.Show(this, $"未能停止任务 \"{row.Name}\"（当前没有可停止的活跃进程或重试任务）。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnRestartClick(object sender, RoutedEventArgs e)
        {
            var row = GetSelectedRow();
            if (row == null) return;

            if (!_scheduler.IsGlobalEnabled)
            {
                MessageBox.Show(this, "任务调度总开关已禁用，无法启动任务！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool ok = _scheduler.RestartTask(row.Name);
            if (!ok)
            {
                MessageBox.Show(this, $"重启任务 \"{row.Name}\" 失败。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnViewLogClick(object sender, RoutedEventArgs e)
        {
            var row = GetSelectedRow();
            if (row == null) return;

            string logFile = Path.Combine(ConfigService.LogsDirPath, $"task-{row.Name}.log");
            if (!File.Exists(logFile))
            {
                MessageBox.Show(this, $"该任务尚未生成运行日志文件：\n{logFile}", "暂无日志", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = logFile, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"打开日志文件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnOpenLogDirClick(object sender, RoutedEventArgs e)
        {
            try
            {
                TaskLogger.EnsureLogDir();
                Process.Start(new ProcessStartInfo { FileName = ConfigService.LogsDirPath, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"打开日志目录失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SaveTasksAndNotify(string selectName)
        {
            string error;
            bool saved;
            try
            {
                saved = TaskConfigService.Save(_tasks, _loadResult, out error);
            }
            catch (Exception ex)
            {
                saved = false;
                error = "保存任务配置文件失败：" + ex.Message;
            }

            if (!saved)
            {
                // 只在这次用户主动保存被拒绝时给提示，不打扰后台运行。
                MessageBox.Show(this, error ?? "保存任务配置文件失败", "保存被拒绝",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                // 不 Reload、不 LoadTasks：保留用户本次编辑，修好文件后可重试。
                return;
            }

            try
            {
                _scheduler?.Reload();
                _onTasksChanged?.Invoke();
                LoadTasks();

                if (!string.IsNullOrEmpty(selectName))
                {
                    var item = _items.FirstOrDefault(i => string.Equals(i.Name, selectName, StringComparison.OrdinalIgnoreCase));
                    if (item != null) TaskListView.SelectedItem = item;
                }
            }
            catch (Exception ex)
            {
                // 落盘已成功，后续刷新失败只记日志，不误报"保存失败"。
                TaskLogger.Warn("system", "post-save reload failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 大盘行展示模型，支持属性通知。
    /// </summary>
    public class TaskRowViewModel : INotifyPropertyChanged
    {
        public TaskDefinition Task { get; }
        private TaskStateSnapshot _snapshot;
        private long? _memoryBytes;   // 物理工作集，仅窗口可见时由采样器回填

        public event PropertyChangedEventHandler PropertyChanged;

        public TaskRowViewModel(TaskDefinition task, TaskStateSnapshot snapshot)
        {
            Task = task ?? new TaskDefinition();
            _snapshot = snapshot ?? new TaskStateSnapshot();
        }

        public void UpdateSnapshot(TaskStateSnapshot snapshot)
        {
            _snapshot = snapshot ?? new TaskStateSnapshot();
            _memoryBytes = null;   // 状态跃迁后旧的内存读数即失效，等待下一次采样
            OnPropertyChanged(nameof(StateDisplayText));
            OnPropertyChanged(nameof(StateDotBrush));
            OnPropertyChanged(nameof(StateTextBrush));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(OutcomeDisplayText));
            OnPropertyChanged(nameof(PidDisplayText));
            OnPropertyChanged(nameof(PidTextBrush));
            OnPropertyChanged(nameof(DurationDisplayText));
            OnPropertyChanged(nameof(MemoryDisplayText));
            OnPropertyChanged(nameof(MemoryTextBrush));
        }

        public void RefreshLiveDuration()
        {
            OnPropertyChanged(nameof(DurationDisplayText));
            OnPropertyChanged(nameof(StateDisplayText));
        }

        protected void OnPropertyChanged([CallerMemberName] string prop = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        }

        public string Name => Task.Name;

        public bool IsRunning => _snapshot.IsRunning;
        public bool IsActive => _snapshot.State == TaskRuntimeState.Running ||
                                _snapshot.State == TaskRuntimeState.Starting ||
                                _snapshot.State == TaskRuntimeState.WaitingRetry;

        // ---- 启用 / 常驻（配置态，值统一为 是 / 否） ----

        public string EnabledBadgeText => Task.Enabled ? "是" : "否";
        public Brush EnabledBadgeBg => Task.Enabled ? new SolidColorBrush(Color.FromRgb(236, 253, 245)) : new SolidColorBrush(Color.FromRgb(241, 245, 249));
        public Brush EnabledBadgeFg => Task.Enabled ? new SolidColorBrush(Color.FromRgb(5, 150, 105)) : new SolidColorBrush(Color.FromRgb(100, 116, 139));

        /// <summary>常驻守护（options.isDetach，进程退出自动重启）。原“模式”列。</summary>
        public bool IsResident => Task.Options?.IsDetach == true;
        public string ResidentText => IsResident ? "是" : "否";
        public Brush ResidentTextBrush => IsResident
            ? new SolidColorBrush(Color.FromRgb(29, 78, 216))
            : new SolidColorBrush(Color.FromRgb(148, 163, 184));

        public string TriggerSummary
        {
            get
            {
                if (Task.Trigger == null) return "未知";
                switch (Task.Trigger.Type)
                {
                    case TaskTriggerType.Startup: return $"启动 {Task.Trigger.DelaySec}s";
                    case TaskTriggerType.Interval: return $"间隔 {Task.Trigger.Every}";
                    case TaskTriggerType.Daily: return $"每天 {Task.Trigger.At}";
                    case TaskTriggerType.Cron: return $"Cron {Task.Trigger.Expr}";
                    case TaskTriggerType.Watch: return $"监听 {Task.Trigger.WatchFilter}";
                    case TaskTriggerType.SessionLock: return "锁屏时";
                    case TaskTriggerType.SessionUnlock: return "解锁时";
                    case TaskTriggerType.Idle: return $"空闲 {Task.Trigger.AfterMinutes}m";
                    case TaskTriggerType.Manual: return "手动运行";
                    case TaskTriggerType.Hotkey: return $"热键 {Task.Trigger.Hotkey}";
                    default: return Task.Trigger.Type.ToString();
                }
            }
        }

        public Brush TriggerBadgeBg => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Task.TriggerBadgeBg));
        public Brush TriggerBadgeFg => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Task.TriggerBadgeFg));

        // ---- 运行时列（PID / 已运行 / 内存占用） ----

        public int? Pid => _snapshot.Pid;

        public string PidDisplayText
        {
            get
            {
                if (!IsRunning) return "--";
                if (_snapshot.ActiveInstances > 1) return "多实例";
                return _snapshot.Pid.HasValue ? _snapshot.Pid.Value.ToString() : "--";
            }
        }

        public Brush PidTextBrush => IsRunning && _snapshot.Pid.HasValue
            ? new SolidColorBrush(Color.FromRgb(51, 65, 85))
            : new SolidColorBrush(Color.FromRgb(148, 163, 184));

        /// <summary>已运行时长，人类可读：45 秒 / 12 分 30 秒 / 3 小时 7 分 / 2 天 8 小时。</summary>
        public string DurationDisplayText => _snapshot.StartedAt.HasValue
            ? FormatDuration(_snapshot.DurationSec)
            : "--";

        /// <summary>内存占用（物理工作集）。仅在窗口可见时由采样器写入，其余情况为 --。</summary>
        public string MemoryDisplayText
        {
            get
            {
                if (!IsRunning) return "--";
                return _memoryBytes.HasValue ? FormatBytes(_memoryBytes.Value) : "--";
            }
        }

        public Brush MemoryTextBrush => _memoryBytes.HasValue && IsRunning
            ? new SolidColorBrush(Color.FromRgb(51, 65, 85))
            : new SolidColorBrush(Color.FromRgb(148, 163, 184));

        /// <summary>由窗口的性能采样器回填（null = 读不到/未运行）。</summary>
        public void SetMemoryBytes(long? bytes)
        {
            if (_memoryBytes == bytes) return;
            _memoryBytes = bytes;
            OnPropertyChanged(nameof(MemoryDisplayText));
            OnPropertyChanged(nameof(MemoryTextBrush));
        }

        internal static string FormatDuration(double totalSeconds)
        {
            if (double.IsNaN(totalSeconds) || totalSeconds < 0) totalSeconds = 0;
            long sec = (long)totalSeconds;

            if (sec < 60) return sec + " 秒";
            if (sec < 3600)
            {
                long m = sec / 60, s = sec % 60;
                return s == 0 ? m + " 分" : m + " 分 " + s + " 秒";
            }
            if (sec < 86400)
            {
                long h = sec / 3600, m = (sec % 3600) / 60;
                return m == 0 ? h + " 小时" : h + " 小时 " + m + " 分";
            }
            long d = sec / 86400, hh = (sec % 86400) / 3600;
            return hh == 0 ? d + " 天" : d + " 天 " + hh + " 小时";
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "--";
            const double KB = 1024d, MB = KB * 1024d, GB = MB * 1024d;
            // 固定不变文化：避免区域小数点差异让同一内存在不同语言下显示成 72,8 MB
            if (bytes >= GB) return (bytes / GB).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " GB";
            if (bytes >= MB) return (bytes / MB).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
            return (bytes / KB).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " KB";
        }

        public string ActionSummary
        {
            get
            {
                string f = Task.Action?.File ?? "";
                string a = Task.Action?.Args ?? "";
                string s = $"{f} {a}".Trim();
                return string.IsNullOrEmpty(s) ? "<未设置>" : s;
            }
        }

        public string StateDisplayText
        {
            get
            {
                switch (_snapshot.State)
                {
                    case TaskRuntimeState.Starting:
                        return "启动中";
                    case TaskRuntimeState.Running:
                        return _snapshot.ActiveInstances > 1 ? $"运行中 ×{_snapshot.ActiveInstances}" : "运行中";
                    case TaskRuntimeState.Stopping:
                        return "停止中";
                    case TaskRuntimeState.WaitingRetry:
                        return $"等待重试 {_snapshot.NextRetryDelaySec}s";
                    case TaskRuntimeState.MarkedFailed:
                        return "熔断保护";
                    default:
                        return "空闲";
                }
            }
        }

        public Brush StateDotBrush
        {
            get
            {
                switch (_snapshot.State)
                {
                    case TaskRuntimeState.Running:
                        return new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Emerald Green
                    case TaskRuntimeState.Starting:
                        return new SolidColorBrush(Color.FromRgb(59, 130, 246)); // Blue
                    case TaskRuntimeState.Stopping:
                        return new SolidColorBrush(Color.FromRgb(239, 68, 68));  // Red
                    case TaskRuntimeState.WaitingRetry:
                        return new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
                    case TaskRuntimeState.MarkedFailed:
                        return new SolidColorBrush(Color.FromRgb(220, 38, 38));  // Dark Red
                    default:
                        return new SolidColorBrush(Color.FromRgb(148, 163, 184)); // Slate 400
                }
            }
        }

        public Brush StateTextBrush
        {
            get
            {
                switch (_snapshot.State)
                {
                    case TaskRuntimeState.Running:
                        return new SolidColorBrush(Color.FromRgb(4, 120, 87));
                    case TaskRuntimeState.Starting:
                        return new SolidColorBrush(Color.FromRgb(29, 78, 216));
                    case TaskRuntimeState.Stopping:
                        return new SolidColorBrush(Color.FromRgb(185, 28, 28));
                    case TaskRuntimeState.WaitingRetry:
                        return new SolidColorBrush(Color.FromRgb(180, 83, 9));
                    case TaskRuntimeState.MarkedFailed:
                        return new SolidColorBrush(Color.FromRgb(153, 27, 27));
                    default:
                        return new SolidColorBrush(Color.FromRgb(100, 116, 139));
                }
            }
        }

        public string OutcomeDisplayText
        {
            get
            {
                if (!string.IsNullOrEmpty(_snapshot.LastOutcome))
                    return _snapshot.LastOutcome;
                if (_snapshot.LastExitCode.HasValue)
                    return $"退出码: {_snapshot.LastExitCode.Value}";
                return "--";
            }
        }
    }
}
