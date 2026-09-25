using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using CarroDesk.Core;
using CarroDesk.Models;
using CarroDesk.Modules.TaskScheduler.Models;
using CarroDesk.Services.Tasks.Triggers;
using CarroDesk.Services.Localization;

namespace CarroDesk.Services.Tasks
{
    public class TaskSchedulerService : IDisposable, ITaskSchedulerService
    {
        private readonly object _lock = new object();
        private List<TaskDefinition> _tasks = new List<TaskDefinition>();
        private List<ITrigger> _triggers = new List<ITrigger>();
        private readonly HashSet<string> _firedStartupTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, bool> _running = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private bool _started;
        private bool _globalEnabled = true;
        private readonly IIdleService _idleService;
        private readonly IConfigManager _configManager;
        private readonly INotificationService _notificationService;
        private readonly Action<string> _balloonNotifier;
        private readonly IHotkeyService _hotkeyService;
        private readonly Dispatcher _dispatcher;

        public bool GlobalEnabled => _globalEnabled;
        public bool IsGlobalEnabled => _globalEnabled;
        public IReadOnlyList<TaskDefinition> Tasks => _tasks.AsReadOnly();

        public TaskSchedulerService(
            IIdleService idleService = null,
            IConfigManager configManager = null,
            INotificationService notificationService = null,
            Action<string> balloonNotifier = null,
            IHotkeyService hotkeyService = null,
            Dispatcher dispatcher = null)
        {
            _idleService = idleService;
            _configManager = configManager;
            _notificationService = notificationService;
            _balloonNotifier = balloonNotifier;
            _hotkeyService = hotkeyService;
            _dispatcher = dispatcher;
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _globalEnabled = GetCurrentTasksEnabled() ?? true;

            TaskLoadResult result = null;
            try { result = TaskConfigService.LoadOrCreate(); } catch { result = new TaskLoadResult(); }
            if (result == null) result = new TaskLoadResult();

            Apply(result);

            // 休眠唤醒后需要补偿 Daily/Cron 的漏触发。此前只有 Stop() 里的 -=，
            // 缺少这里的 +=，导致 OnPowerModeChanged 永不触发、补偿逻辑整体失效
            // （笔记本合盖跨过定时点后任务永久漏执行且无任何日志）。
            try { SystemEvents.PowerModeChanged += OnPowerModeChanged; }
            catch (Exception ex) { TaskLogger.Warn("system", "subscribe PowerModeChanged failed: " + ex.Message); }

            TaskLogger.Info("system", "TaskScheduler started, tasks=" + _tasks.Count + ", errors=" + result.Errors.Count + ", globalEnabled=" + _globalEnabled);
            foreach (var e in result.Errors) TaskLogger.Warn("system", e);
            if (result.FileCreated) TaskLogger.Info("system", "tasks.json created at " + TaskConfigService.FilePath);
            NotifyLoadIssues(result);
        }

        private bool? GetCurrentTasksEnabled()
        {
            try
            {
                if (_configManager != null)
                {
                    var cfg = _configManager.GetModuleConfig<TaskSchedulerConfig>("TaskScheduler");
                    return cfg?.GlobalEnabled;
                }
            }
            catch { }
            return null;
        }

        public bool SetGlobalEnabled(bool enabled)
        {
            bool changed = false;
            lock (_lock) { if (_globalEnabled != enabled) { _globalEnabled = enabled; changed = true; } }
            if (!changed) return true;
            if (enabled)
            {
                // re-apply current tasks to recreate triggers
                TaskLoadResult result = null;
                try { result = TaskConfigService.Load(); } catch { result = new TaskLoadResult(); }
                if (result != null) Apply(result);
                TaskLogger.Info("system", "TaskScheduler global enabled");
            }
            else
            {
                StopTriggers();
                TaskLogger.Info("system", "TaskScheduler global disabled");
            }
            // persist to config.json if possible
            try
            {
                if (_configManager != null)
                {
                    var cfg = _configManager.GetModuleConfig<TaskSchedulerConfig>("TaskScheduler") ?? new TaskSchedulerConfig();
                    if (cfg.GlobalEnabled != enabled)
                    {
                        cfg.GlobalEnabled = enabled;
                        if (!_configManager.SaveModuleConfig("TaskScheduler", cfg))
                        {
                            // 持久化失败时恢复运行时开关和触发器，避免本次进程与下次启动状态分叉。
                            SetGlobalEnabled(!enabled);
                            TaskLogger.Error("system", "TaskScheduler global switch persistence failed; runtime state rolled back");
                            return false;
                        }
                    }
                }
            }
            catch
            {
                SetGlobalEnabled(!enabled);
                return false;
            }
            return true;
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            StopTriggers();
            TaskLogger.Info("system", "TaskScheduler stopped");
        }

        TaskReloadResult ITaskSchedulerService.Reload()
        {
            var r = Reload();
            return new TaskReloadResult
            {
                Tasks = r?.Tasks != null ? r.Tasks.Count : 0,
                Errors = r?.Errors != null ? r.Errors.Count : 0
            };
        }

        public TaskLoadResult Reload()
        {
            var result = TaskConfigService.Load();
            // refresh global flag from config in case it was edited externally
            try
            {
                var cur = GetCurrentTasksEnabled();
                if (cur.HasValue) _globalEnabled = cur.Value;
            }
            catch { }
            Apply(result);
            TaskLogger.Info("system", "TaskScheduler reloaded, tasks=" + _tasks.Count + ", errors=" + result.Errors.Count + ", globalEnabled=" + _globalEnabled);
            foreach (var e in result.Errors) TaskLogger.Warn("system", e);
            NotifyLoadIssues(result);
            return result;
        }

        /// <summary>
        /// 串行化 <see cref="Apply"/>。
        ///
        /// Apply 可从多条路径并发进入：Start()、Reload()、SetGlobalEnabled(true)、
        /// 以及模块的 OnConfigReloaded 回调。两个 Apply 交错会出现：
        /// A 的 StopTriggers 释放掉 B 刚建好的触发器、Fired 被重复绑定、
        /// _triggers 被后写覆盖导致部分触发器无人释放（Stop 时无法解绑 → 泄漏 + 幽灵触发）。
        ///
        /// 锁序约定：_applyLock → _lock（Apply 内部会取 _lock）。
        /// 现有代码中没有任何路径在持有 _lock 时调用 Apply，故不存在反向嵌套。
        /// </summary>
        private readonly object _applyLock = new object();

        private void Apply(TaskLoadResult result)
        {
            var dispatcher = _dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                // DispatcherTimer 必须在有消息泵的 UI Dispatcher 上创建；公开 Reload/SetGlobalEnabled
                // 可能从后台线程调用，统一封送避免创建永远不会 Tick 的后台 Dispatcher。
                dispatcher.Invoke(new Action(() => Apply(result)));
                return;
            }
            ApplyCore(result);
        }

        private void ApplyCore(TaskLoadResult result)
        {
            lock (_applyLock)
            {
                ApplyCoreLocked(result);
            }
        }

        private void ApplyCoreLocked(TaskLoadResult result)
        {
            StopTriggers();
            lock (_lock)
            {
                _tasks = result.Tasks ?? new List<TaskDefinition>();
            }
            if (!_globalEnabled)
            {
                lock (_lock) _triggers = new List<ITrigger>();
                TaskLogger.Info("system", "TaskScheduler disabled globally, triggers not started");
                return;
            }
            // create triggers for enabled tasks
            var newTriggers = new List<ITrigger>();
            foreach (var task in _tasks)
            {
                if (!task.Enabled) continue;
                ITrigger trig = CreateTrigger(task);
                if (trig == null) continue;
                trig.Fired += OnTriggerFired;
                newTriggers.Add(trig);
            }
            foreach (var t in newTriggers)
            {
                try { t.Start(); } catch (Exception ex) { TaskLogger.Error(t.Task.Name, "trigger start failed: " + ex.Message); }
            }
            lock (_lock) _triggers = newTriggers;
        }

        private ITrigger CreateTrigger(TaskDefinition task)
        {
            switch (task.Trigger.Type)
            {
                case TaskTriggerType.Startup:
                    lock (_lock)
                    {
                        if (_firedStartupTasks.Contains(task.Name))
                            return null;
                    }
                    return new StartupTrigger(task);
                case TaskTriggerType.Interval: return new IntervalTrigger(task);
                case TaskTriggerType.Daily: return new DailyTrigger(task);
                case TaskTriggerType.Cron: return new CronTrigger(task);
                case TaskTriggerType.SessionLock:
                case TaskTriggerType.SessionUnlock: return new SessionEventTrigger(task);
                case TaskTriggerType.Idle: return new IdleTrigger(task, _idleService);
                case TaskTriggerType.Manual: return new ManualTrigger(task);
                case TaskTriggerType.Hotkey: return new HotkeyTrigger(task, _hotkeyService);
                case TaskTriggerType.Watch: return new FileWatcherTrigger(task);
                default:
                    lock (_lock)
                    {
                        if (_firedStartupTasks.Contains(task.Name))
                            return null;
                    }
                    return new StartupTrigger(task);
            }
        }

        public IReadOnlyList<TaskDefinition> GetManualTasks()
        {
            lock (_lock)
            {
                var list = new List<TaskDefinition>();
                foreach (var t in _tasks) if (t.Trigger.Type == TaskTriggerType.Manual && t.Enabled) list.Add(t);
                return list.AsReadOnly();
            }
        }

        public bool RunManual(string name)
        {
            TaskDefinition task = null;
            lock (_lock)
            {
                foreach (var t in _tasks) if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) { task = t; break; }
            }
            if (task == null) return false;
            if (!_globalEnabled) return false;
            if (!task.Enabled) return false;
            // check condition
            string skip;
            if (!TaskConditionEvaluator.ShouldRun(task, out skip))
            {
                TaskLogger.Warn(task.Name, "manual skipped: " + skip);
                return false;
            }
            Task.Run(() => ExecuteAsync(task, "manual"));
            return true;
        }

        public void RecordFailureNotify(TaskDefinition task, int code)
        {
            try
            {
                if (task.Options != null && !task.Options.NotifyOnFailure) return;
                if (code == 0) return;
                Notify(Loc.T("Tasks.RunFailed", "任务失败 [{0}] exit={1}，详见 logs/task-{2}.log", task.Name, code, task.Name));
            }
            catch { }
        }

        private void Notify(string message)
        {
            try
            {
                if (_notificationService != null)
                {
                    _notificationService.Show(message, "CarroDesk");
                }
                else if (_balloonNotifier != null)
                {
                    _balloonNotifier(message);
                }
            }
            catch { }
        }

        /// <summary>
        /// 加载期问题中对用户可见的部分：文件损坏并已自愈时必须提示，
        /// 否则用户看到的是"任务列表突然清空"而不知道备份在哪。
        /// </summary>
        private void NotifyLoadIssues(TaskLoadResult result)
        {
            if (result == null) return;

            if (result.FileRecovered)
            {
                Notify(Loc.T("Tasks.ConfigRecovered",
                    "tasks.json 已损坏，已备份到 {0} 并重建（任务列表已重置，请检查备份）",
                    result.RecoveredBackupPath));
            }
        }

        private void StopTriggers()
        {
            var dispatcher = _dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(new Action(StopTriggers));
                return;
            }
            // 与 Apply 共用同一把串行化锁：否则 Stop()/禁用总开关 与 Apply 交错时，
            // 可能释放掉对方刚创建的触发器。Monitor 可重入，ApplyCore 内部再次进入是安全的。
            lock (_applyLock)
            {
                List<ITrigger> old;
                lock (_lock) { old = _triggers; _triggers = new List<ITrigger>(); }
                foreach (var t in old)
                {
                    try { t.Fired -= OnTriggerFired; } catch { }
                    try { t.Stop(); } catch { }
                    try { t.Dispose(); } catch { }
                }
            }
        }

        private void OnTriggerFired(TaskDefinition task, string reason)
        {
            if (task != null && !string.IsNullOrEmpty(task.Name) &&
                reason != null && reason.StartsWith("startup", StringComparison.OrdinalIgnoreCase))
            {
                lock (_lock)
                {
                    _firedStartupTasks.Add(task.Name);
                }
            }

            // dispatch to thread pool, avoid blocking trigger thread (especially UI timer)
            var t = task;
            var r = reason;
            Task.Run(() => ExecuteAsync(t, r));
        }

        private async Task ExecuteAsync(TaskDefinition task, string reason)
        {
            if (!_globalEnabled || !IsTaskStillEnabled(task))
                return;

            // condition check
            string skipReason;
            if (!TaskConditionEvaluator.ShouldRun(task, out skipReason))
            {
                TaskLogger.Info(task.Name, "skipped(" + reason + ") condition not met: " + skipReason);
                return;
            }

            bool skip = false;
            lock (_lock)
            {
                bool isRunning;
                if (_running.TryGetValue(task.Name, out isRunning) && isRunning)
                {
                    bool allow = task.Options != null && task.Options.AllowConcurrent;
                    if (!allow) skip = true;
                }
                if (!skip) _running[task.Name] = true;
            }
            if (skip)
            {
                TaskLogger.Warn(task.Name, "skipped(" + reason + ") concurrent execution not allowed");
                return;
            }
            int lastCode = 0;
            try
            {
                int retry = task.Options != null ? task.Options.Retry : 0;
                int attempt = 0;
                while (true)
                {
                    if (!_globalEnabled || !IsTaskStillEnabled(task))
                    {
                        TaskLogger.Info(task.Name, "retry cancelled because task was disabled, removed, or scheduler stopped");
                        return;
                    }
                    attempt++;
                    int code = 0;
                    try
                    {
                        code = await TaskRunner.RunAsync(task, reason + (attempt > 1 ? " retry#" + attempt : "")).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        TaskLogger.Error(task.Name, "run exception: " + ex);
                        code = -1;
                    }
                    lastCode = code;
                    if (code == 0 || attempt > retry) break;
                    TaskLogger.Info(task.Name, "retry " + attempt + "/" + retry + " after non-zero exit " + code);
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_lock) _running[task.Name] = false;
            }
            // failure notify
            try
            {
                if (lastCode != 0)
                {
                    RecordFailureNotify(task, lastCode);
                    // also keep recent list for tray
                    lock (_lock)
                    {
                        AddRecent(task.Name, lastCode);
                    }
                }
                else
                {
                    lock (_lock) AddRecent(task.Name, 0);
                }
            }
            catch { }
        }

        private bool IsTaskStillEnabled(TaskDefinition task)
        {
            if (task == null) return false;
            lock (_lock)
            {
                return _started && _globalEnabled && _tasks.Any(t =>
                    ReferenceEquals(t, task) && t.Enabled);
            }
        }

        private List<string> _recent = new List<string>();
        private void AddRecent(string name, int code)
        {
            string entry = string.Format("{0:HH:mm:ss} {1} {2}", DateTime.Now, name, code == 0 ? "ok" : "fail:" + code);
            _recent.Insert(0, entry);
            if (_recent.Count > 10) _recent.RemoveAt(10);
        }
        public IReadOnlyList<string> GetRecent()
        {
            lock (_lock) return _recent.AsReadOnly();
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;

            // SystemEvents 在专用线程上触发本回调，而 DispatcherTimer 必须在已启动消息泵的
            // UI 线程上创建：在 SystemEvents 线程上 new DispatcherTimer 会隐式创建该线程的
            // Dispatcher（没有 Run 循环），Tick 永远不会触发。
            var dispatcher = _dispatcher ?? System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                TaskLogger.Warn("system", "resume catch-up skipped: no dispatcher available");
                return;
            }
            if (!dispatcher.CheckAccess())
            {
                try { dispatcher.BeginInvoke(new Action(() => OnPowerModeChanged(sender, e))); }
                catch (Exception ex) { TaskLogger.Warn("system", "resume catch-up dispatch failed: " + ex.Message); }
                return;
            }

            // delay a bit for system to stabilize, then catch up daily/cron
            var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (s, args) =>
            {
                try { timer.Stop(); } catch { }
                try
                {
                    lock (_lock)
                    {
                        foreach (var trig in _triggers)
                        {
                            var d = trig as DailyTrigger;
                            if (d != null) d.CheckCatchUp();
                            var c = trig as CronTrigger;
                            if (c != null) c.CheckCatchUp();
                        }
                    }
                    TaskLogger.Info("system", "resume catch-up checked");
                }
                catch (Exception ex) { TaskLogger.Warn("system", "resume catch-up failed: " + ex.Message); }
            };
            timer.Start();
        }

        public void Dispose() { Stop(); }
    }
}
