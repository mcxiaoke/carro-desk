using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
        private readonly DispatcherTimer _ticker;

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

            // 1s 轻量心跳定时器：仅用于就地递增运行中/重试任务的秒数显示
            _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _ticker.Tick += (s, e) =>
            {
                foreach (var item in _items)
                {
                    if (item.IsActive)
                    {
                        item.RefreshLiveDuration();
                    }
                }
            };
            _ticker.Start();

            Closed += (s, e) =>
            {
                _ticker.Stop();
                if (_scheduler != null)
                {
                    _scheduler.StateChanged -= OnSchedulerStateChanged;
                }
            };

            LoadTasks();
        }

        private void OnSchedulerStateChanged(string taskName, TaskRuntimeState newState, TaskStateSnapshot snapshot)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var item = _items.FirstOrDefault(i => string.Equals(i.Name, taskName, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    item.UpdateSnapshot(snapshot);
                    UpdateToolbarButtons();
                    UpdateStatsSummary();
                }
            }));
        }

        public void LoadTasks()
        {
            TaskLoadResult result = null;
            try { result = TaskConfigService.Load(); } catch { }
            _tasks = result?.Tasks ?? new List<TaskDefinition>();

            ApplyFilterAndRefreshList();
            UpdateStatsSummary();
            UpdateToolbarButtons();
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
            ApplyFilterAndRefreshList();
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
            try
            {
                TaskConfigService.Save(_tasks);
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
                MessageBox.Show(this, $"保存任务配置文件失败:\n{ex.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
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

        public event PropertyChangedEventHandler PropertyChanged;

        public TaskRowViewModel(TaskDefinition task, TaskStateSnapshot snapshot)
        {
            Task = task ?? new TaskDefinition();
            _snapshot = snapshot ?? new TaskStateSnapshot();
        }

        public void UpdateSnapshot(TaskStateSnapshot snapshot)
        {
            _snapshot = snapshot ?? new TaskStateSnapshot();
            OnPropertyChanged(nameof(StateDisplayText));
            OnPropertyChanged(nameof(StateDotBrush));
            OnPropertyChanged(nameof(StateTextBrush));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(OutcomeDisplayText));
        }

        public void RefreshLiveDuration()
        {
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

        public string EnabledBadgeText => Task.Enabled ? "已启用" : "已禁用";
        public Brush EnabledBadgeBg => Task.Enabled ? new SolidColorBrush(Color.FromRgb(236, 253, 245)) : new SolidColorBrush(Color.FromRgb(241, 245, 249));
        public Brush EnabledBadgeFg => Task.Enabled ? new SolidColorBrush(Color.FromRgb(5, 150, 105)) : new SolidColorBrush(Color.FromRgb(100, 116, 139));

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

        public string ModeDisplayText => Task.Options?.IsDetach == true ? "常驻守护" : "单次运行";

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
                        return "启动中...";
                    case TaskRuntimeState.Running:
                        if (_snapshot.ActiveInstances > 1)
                            return $"运行中 ({_snapshot.ActiveInstances}个 · {_snapshot.DurationSec:0}s)";
                        if (_snapshot.Pid.HasValue)
                            return $"运行中 (PID {_snapshot.Pid} · {_snapshot.DurationSec:0}s)";
                        return $"运行中 ({_snapshot.DurationSec:0}s)";
                    case TaskRuntimeState.Stopping:
                        return "停止中...";
                    case TaskRuntimeState.WaitingRetry:
                        return $"等待重试 ({_snapshot.NextRetryDelaySec}s)";
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
