using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

        /// <summary>
        /// 当天已由 DailyTrigger 正常触发过的任务（任务名 → 触发日期）。
        /// CatchUpMissed 的补跑必须同时参考它：否则当天已经正常跑过一次后，
        /// 任何配置重载都会再补一发，同一任务一天执行两次（H1）。
        /// </summary>
        private readonly Dictionary<string, DateTime> _lastDailyFire =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// 运行表：任务名 → 该任务当前在册的运行槽列表。
        ///
        /// 为什么是列表而不是单槽：allowConcurrent=true 时同一任务允许多个实例并存，
        /// 原实现按任务名只留一个槽（后来的覆盖先来的），被覆盖的实例句柄彻底失联 ——
        /// GetRunning/TryStop 只能看到最后一个，用户既查不到也停不掉。
        /// detach + allowConcurrent 已被 <see cref="TaskDefinition.Validate"/> 拒绝，
        /// 因此多实例只可能出现在 wait 模式，不涉及守护/重启链。
        /// </summary>
        private readonly Dictionary<string, List<RunSlot>> _running =
            new Dictionary<string, List<RunSlot>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, TaskStateMachine> _stateMachines =
            new Dictionary<string, TaskStateMachine>(StringComparer.OrdinalIgnoreCase);

        public event Action<string, TaskRuntimeState, TaskStateSnapshot> StateChanged;

        /// <summary>
        /// 单任务并发实例上限（仅对 allowConcurrent 生效）。
        /// 没有上限时"每分钟触发一次 + 任务跑一小时"会无界堆积进程。
        /// 上限只拦新实例，不影响已跑着的实例被查询/停止。
        /// </summary>
        private const int MaxConcurrentInstances = 5;
        private bool _started;
        private bool _globalEnabled = true;

        /// <summary>宿主本次会话的启动时刻，作为 startup 触发器 delaySec 的绝对基准（跨 Apply 不变）。</summary>
        private DateTime? _hostStartedAt;
        private readonly IIdleService _idleService;
        private readonly IConfigManager _configManager;
        private readonly INotificationService _notificationService;
        private readonly Action<string> _balloonNotifier;
        private readonly IHotkeyService _hotkeyService;
        private readonly Dispatcher _dispatcher;

        /// <summary>
        /// 任务级触发频率限制。必须由服务层单例持有：ApplyCoreLocked 每次配置重载都会
        /// 整体重建触发器，状态放触发器实例里会被清空（保存一次任务即可绕过限流）。
        /// 构造可注入以便单测使用纯内存态。
        /// </summary>
        private readonly TaskFireLimiter _fireLimiter;

        /// <summary>触发器 last-fired 状态仓库，服务层持有，不随 Apply 重建触发器而丢失。</summary>
        private readonly ITriggerStateStore _triggerState;

        public bool GlobalEnabled => _globalEnabled;
        public bool IsGlobalEnabled => _globalEnabled;
        public IReadOnlyList<TaskDefinition> Tasks
        {
            get { lock (_lock) return _tasks.ToArray(); }
        }

        public TaskStateMachine GetOrCreateFsm(string taskName)
        {
            if (string.IsNullOrEmpty(taskName)) return new TaskStateMachine("");
            lock (_lock)
            {
                if (!_stateMachines.TryGetValue(taskName, out var fsm))
                {
                    fsm = new TaskStateMachine(taskName);
                    _stateMachines[taskName] = fsm;
                }
                return fsm;
            }
        }

        public TaskStateSnapshot GetState(string taskName)
        {
            if (string.IsNullOrEmpty(taskName)) return new TaskStateSnapshot();
            return GetOrCreateFsm(taskName).GetSnapshot();
        }

        public IReadOnlyDictionary<string, TaskStateSnapshot> GetAllStates()
        {
            lock (_lock)
            {
                var dict = new Dictionary<string, TaskStateSnapshot>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in _tasks)
                {
                    dict[t.Name] = GetOrCreateFsm(t.Name).GetSnapshot();
                }
                return dict;
            }
        }

        public void NotifyStateChanged(string taskName)
        {
            if (string.IsNullOrEmpty(taskName)) return;
            var fsm = GetOrCreateFsm(taskName);
            var snapshot = fsm.GetSnapshot();
            try { StateChanged?.Invoke(taskName, snapshot.State, snapshot); } catch { }
        }

        public TaskSchedulerService(
            IIdleService idleService = null,
            IConfigManager configManager = null,
            INotificationService notificationService = null,
            Action<string> balloonNotifier = null,
            IHotkeyService hotkeyService = null,
            Dispatcher dispatcher = null,
            TaskFireLimiter fireLimiter = null,
            ITriggerStateStore triggerState = null)
        {
            _idleService = idleService;
            _configManager = configManager;
            _notificationService = notificationService;
            _balloonNotifier = balloonNotifier;
            _hotkeyService = hotkeyService;
            _dispatcher = dispatcher;
            _fireLimiter = fireLimiter ?? new TaskFireLimiter(TaskFireLimiter.DefaultStatePath);
            _triggerState = triggerState ?? new PersistentTriggerStateStore();
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            // 只记录一次：Reload / SetGlobalEnabled 触发的 Apply 不得移动 startup 延迟基准
            if (!_hostStartedAt.HasValue) _hostStartedAt = DateTime.Now;
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
            return SetGlobalEnabled(enabled, false);
        }

        /// <summary>
        /// isRollback=true 表示本调用是"持久化失败后的运行时状态回滚"，
        /// 此时只恢复内存开关与触发器，不再尝试持久化——否则持久化持续失败会
        /// 形成"保存失败→回滚→保存失败→回滚"的相互递归直至栈溢出。
        /// </summary>
        private bool SetGlobalEnabled(bool enabled, bool isRollback)
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
            if (isRollback) return false;
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
                            SetGlobalEnabled(!enabled, true);
                            TaskLogger.Error("system", "TaskScheduler global switch persistence failed; runtime state rolled back");
                            return false;
                        }
                    }
                }
            }
            catch
            {
                SetGlobalEnabled(!enabled, true);
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
            StopRunningInstances();
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
            var newTasks = result.Tasks ?? new List<TaskDefinition>();

            // 频率限制状态按任务名聚合、跨 Apply 存活：这里只清理已删除任务的条目，
            // 绝不清空额度，否则「保存任务 / 配置热重载」就能绕过每日上限。
            // 放在锁外：ForgetRemovedTasks 可能落盘，不应持 _lock 做文件 IO。
            var activeNames = newTasks
                .Where(t => t != null && t.Enabled && !string.IsNullOrEmpty(t.Name))
                .Select(t => t.Name)
                .ToArray();
            _fireLimiter.ForgetRemovedTasks(activeNames);
            _triggerState.ForgetRemoved(activeNames);

            var toKill = new List<KeyValuePair<string, TaskProcessHandle>>();

            lock (_lock)
            {
                // 配置中已删除或禁用的任务，其运行实例必须一起停掉（H2）：
                // 否则旧进程继续运行并占着运行槽，同名新任务无法启动、
                // 删除的任务也无法从"运行中"消失，只能等进程自然退出或重启宿主。
                var enabledNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in newTasks)
                {
                    if (t != null && t.Enabled && !string.IsNullOrEmpty(t.Name)) enabledNames.Add(t.Name);
                }

                List<string> orphaned = null;
                foreach (var pair in _running)
                {
                    if (!enabledNames.Contains(pair.Key)) (orphaned ?? (orphaned = new List<string>())).Add(pair.Key);
                }
                if (orphaned != null)
                {
                    foreach (var name in orphaned)
                    {
                        List<RunSlot> slots;
                        if (!_running.TryGetValue(name, out slots)) continue;
                        _running.Remove(name);
                        foreach (var s in slots)
                        {
                            if (s == null) continue;
                            s.StopRequested = true;
                            var h = s.Handle;
                            s.Handle = null;
                            if (h != null) toKill.Add(new KeyValuePair<string, TaskProcessHandle>(name, h));
                        }
                    }
                }

                _tasks = newTasks;
                // 配置变化 = 用户干预信号：清空守护计数与失败标记（同"保存任务即可重置"的语义）
                _supervision.Clear();
                foreach (var fsm in _stateMachines.Values)
                {
                    fsm.Reset();
                }
            }

            if (toKill.Count > 0)
            {
                TaskLogger.Info("system", "config change stopped " + toKill.Count +
                    " running instance(s) of removed/disabled tasks");
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    foreach (var kv in toKill)
                    {
                        try { kv.Value.Stop(); } catch { }
                        try
                        {
                            GetOrCreateFsm(kv.Key).OnStopCompleted();
                            NotifyStateChanged(kv.Key);
                        }
                        catch { }
                    }
                });
            }

            if (!_globalEnabled)
            {
                lock (_lock) _triggers = new List<ITrigger>();
                TaskLogger.Info("system", "TaskScheduler disabled globally, triggers not started");
                return;
            }
            // create triggers for enabled tasks
            var newTriggers = new List<ITrigger>();
            foreach (var task in newTasks)
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

            // CatchUpMissed / RunAtStartup 的"启动补跑"由此统一投递（M3）：
            // 以 _firedStartupTasks 作会话级集合（reason 以 "startup" 开头即记录，Apply 不清除），
            // 保证每次宿主会话只补一发；触发器重建（任何重载）不再自带立即触发。
            foreach (var task in newTasks)
            {
                if (!task.Enabled || task.Options == null) continue;
                string reason = null;
                if (task.Options.RunAtStartup && task.Trigger != null && task.Trigger.Type == TaskTriggerType.Interval)
                {
                    reason = "startup:runAtStartup";
                }
                else if (task.Options.CatchUpMissed && task.Trigger != null && task.Trigger.Type == TaskTriggerType.Daily)
                {
                    TimeSpan at;
                    if (TaskDefinition.TryParseTime(task.Trigger.At, out at) && DateTime.Now >= DateTime.Now.Date + at)
                    {
                        // 当天已正常触发过的任务不补跑：_firedStartupTasks 只在 startup 触发时登记，
                        // 正常 DailyTrigger 的 reason 是 "daily:HH:mm"，必须另查 _lastDailyFire（H1）。
                        bool firedToday;
                        lock (_lock)
                        {
                            DateTime last;
                            firedToday = _lastDailyFire.TryGetValue(task.Name, out last) && last.Date == DateTime.Today;
                        }
                        if (!firedToday) reason = "startup:catchup";
                    }
                }
                if (reason == null) continue;

                bool alreadyFired;
                lock (_lock) alreadyFired = _firedStartupTasks.Contains(task.Name);
                if (alreadyFired) continue;

                TaskLogger.Info(task.Name, "dispatching one-time startup catch-up (" + reason + ")");
                OnTriggerFired(task, reason);
            }
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
                    return new StartupTrigger(task, _hostStartedAt);
                case TaskTriggerType.Interval: return new IntervalTrigger(task);
                case TaskTriggerType.Daily: return new DailyTrigger(task, _triggerState);
                case TaskTriggerType.Cron: return new CronTrigger(task, _triggerState);
                case TaskTriggerType.SessionLock:
                case TaskTriggerType.SessionUnlock: return new SessionEventTrigger(task);
                case TaskTriggerType.Idle: return new IdleTrigger(task, _idleService, _fireLimiter);
                case TaskTriggerType.Manual: return new ManualTrigger(task);
                case TaskTriggerType.Hotkey: return new HotkeyTrigger(task, _hotkeyService);
                case TaskTriggerType.Watch: return new FileWatcherTrigger(task);
                default:
                    lock (_lock)
                    {
                        if (_firedStartupTasks.Contains(task.Name))
                            return null;
                    }
                    return new StartupTrigger(task, _hostStartedAt);
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
            // RunManual 不经过 OnTriggerFired，limitScope=all 时必须在这里补上门。
            string fireRefusal;
            if (!TryPassFireLimit(task, "manual", out fireRefusal))
            {
                TaskLogger.Info(task.Name, "skipped(manual) fire limit: " + fireRefusal);
                AddRecent(task.Name, "throttled");
                return false;
            }
            Task.Run(() => ExecuteAsync(task, "manual"));
            return true;
        }

        public bool RestartTask(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            TaskDefinition task = null;
            lock (_lock)
            {
                foreach (var t in _tasks) if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) { task = t; break; }
            }
            if (task == null || !_globalEnabled || !task.Enabled) return false;

            // 同步入口先判限流：被拒时不应先停掉旧实例再告知用户失败。
            string fireRefusal;
            if (!TryPassFireLimit(task, "restart", out fireRefusal))
            {
                TaskLogger.Info(task.Name, "skipped(restart) fire limit: " + fireRefusal);
                AddRecent(task.Name, "throttled");
                return false;
            }

            var restartTask = task;
            _ = Task.Run(async () =>
            {
                try
                {
                    // 必须确认旧实例真正退出、槽位已释放后再启动（H3）：
                    // 固定 300ms 在慢退出场景下会让新实例因旧槽未释放被拒，
                    // 或旧进程尚未退出就拉起新实例（双实例/端口冲突）。
                    bool stopped = await StopAndWaitAsync(name, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    if (!stopped)
                    {
                        TaskLogger.Error(name, "restart aborted: previous instance did not exit within 10s");
                        Notify(Loc.T("Tasks.RestartTimeout",
                            "任务 [{0}] 停止超时，已取消重启（请稍后重试）", name));
                        return;
                    }
                    await ExecuteAsync(restartTask, "restart").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    try { TaskLogger.Error(name, "restart error: " + ex); } catch { }
                }
            });
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
            if (task != null && !string.IsNullOrEmpty(task.Name) && reason != null)
            {
                var info = TriggerReason.Parse(reason);
                if (info.IsStartup)
                {
                    lock (_lock)
                    {
                        _firedStartupTasks.Add(task.Name);
                    }
                }
                else if (info.IsDaily)
                {
                    // 记录"当天已正常触发"，供 ApplyCoreLocked 的 CatchUpMissed 判断（H1）。
                    lock (_lock)
                    {
                        _lastDailyFire[task.Name] = DateTime.Today;
                    }
                }
            }

            // 频率限制统一门：插在 startup/daily 会话记录之后、Task.Run 之前。
            // 被限流的 startup:catchup 仍记为「本会话已尝试补跑」，语义与 _firedStartupTasks 一致。
            string fireRefusal;
            if (!TryPassFireLimit(task, reason, out fireRefusal))
            {
                TaskLogger.Info(task.Name, "skipped(" + reason + ") fire limit: " + fireRefusal);
                AddRecent(task.Name, "throttled");
                return;
            }

            // dispatch to thread pool, avoid blocking trigger thread (especially UI timer)
            var t = task;
            var r = reason;
            Task.Run(() => ExecuteAsync(t, r));
        }

        /// <summary>
        /// 所有触发（含 RunManual / RestartTask 旁路）共用的限流准入。
        /// internal 以便单测直接验证策略分流，不触发真实进程启动。
        /// </summary>
        internal bool TryPassFireLimit(TaskDefinition task, string reason, out string refusal)
        {
            refusal = null;
            if (task == null || task.Options == null) return true;

            var policy = new FireLimitPolicy
            {
                MaxRunsPerDay = task.Options.MaxRunsPerDay,
                MinIntervalSec = task.Options.MinIntervalSec
            };
            if (policy.IsUnlimited) return true;

            // limitScope=auto：人工类触发绕过且不消耗配额；all：全部触发受限。
            if (TaskFireLimiter.IsUserDrivenReason(reason) && !task.Options.LimitAppliesToUserDriven)
                return true;

            return _fireLimiter.TryAcquire(task.Name, policy, reason, out refusal);
        }

        private async Task ExecuteAsync(TaskDefinition task, string reason)
        {
            if (!_globalEnabled || !IsTaskStillEnabled(task))
                return;

            var fsm = GetOrCreateFsm(task.Name);
            // 统一前缀判定：HotkeyTrigger 的 reason 是 "hotkey:<组合键>"，
            // 旧实现用 string.Equals 恒为 false，热键在熔断/等待重试期会被误当自动触发（附录 B）。
            bool userDriven = TriggerReason.IsUserDriven(reason);

            bool allowConcurrent = task.Options != null && task.Options.AllowConcurrent;
            string refusal;
            if (!fsm.TryBeginStarting(userDriven, allowConcurrent, out refusal))
            {
                TaskLogger.Warn(task.Name, "skipped(" + reason + ") " + refusal);
                return;
            }
            NotifyStateChanged(task.Name);

            // condition check
            string skipReason;
            if (!TaskConditionEvaluator.ShouldRun(task, out skipReason))
            {
                fsm.OnAbandon();
                NotifyStateChanged(task.Name);
                TaskLogger.Info(task.Name, "skipped(" + reason + ") condition not met: " + skipReason);
                return;
            }

            // 占槽即防重：槽在进程真正启动前就存在，启动窗口期内的并发触发不会漏判。
            // allowConcurrent 时同一任务可有多实例并存，故登记进"该任务的槽列表"。
            RunSlot slot = new RunSlot();
            string slotRefusal;
            if (!TryOccupySlot(task.Name, task.Options != null && task.Options.AllowConcurrent, slot, out slotRefusal))
            {
                fsm.OnAbandon();
                NotifyStateChanged(task.Name);
                TaskLogger.Warn(task.Name, "skipped(" + reason + ") " + slotRefusal);
                return;
            }

            // detach（常驻）模式：启动即返回，不等退出；配置校验已拒绝 detach+retry/timeout 组合
            if (task.Options != null && task.Options.IsDetach)
            {
                TaskProcessHandle handle;
                int code = TaskRunner.StartDetached(task, reason, out handle);
                if (code == TaskRunner.StartSkippedSingleInstance)
                {
                    // 单实例互斥：已有实例在运行（含跨宿主重启的旧实例），不算失败、不计数
                    RemoveSlot(task.Name, slot);
                    fsm.OnAbandon();
                    NotifyStateChanged(task.Name);
                    AddRecent(task.Name, "skipped");
                    return;
                }
                if (code != 0 || handle == null)
                {
                    int delay = task.Options.RestartDelaySec;
                    int limit = task.Options.RestartLimit;
                    var newState = fsm.OnStartFailed(task.Options.RestartOnFailure, delay, limit, "failed to start");
                    NotifyStateChanged(task.Name);

                    if (task.Options.RestartOnFailure && newState == TaskRuntimeState.WaitingRetry)
                    {
                        slot.Handle = null;
                        var t = task;
                        var s = slot;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, delay))).ConfigureAwait(false);
                                RestartContinue(t, s);
                            }
                            catch { }
                        });
                    }
                    else
                    {
                        RemoveSlot(task.Name, slot);
                        AddRecent(task.Name, "fail");
                        RecordFailureNotify(task, -1);
                    }
                    return;
                }
                AttachDetached(task, slot, handle);
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
                    if (slot.StopRequested)
                    {
                        // 用户已请求停止：取消挂起的自动重启（systemctl stop 语义）
                        TaskLogger.Info(task.Name, "stopped by user request; pending retry cancelled");
                        return;
                    }
                    attempt++;
                    int code = 0;
                    try
                    {
                        code = await TaskRunner.RunAsync(
                            task,
                            reason + (attempt > 1 ? " retry#" + attempt : ""),
                            CancellationToken.None, null,
                            h => {
                                // 启动窗口期停止竞态（H2）：TryStop 可能在进程 spawn 完成前
                                // 摘除槽并置位 StopRequested——此时必须立即终止刚启动的进程，
                                // 不得登记回运行表（否则脱离注册：GetRunning 看不到、TryStop
                                // 永远找不到、守护链也被取消）。
                                if (slot.StopRequested)
                                {
                                    TaskLogger.Info(task.Name, "process " + h.Pid + " spawned after user stop request; terminating immediately");
                                    try { h.Stop(); } catch { }
                                    RemoveSlot(task.Name, slot);
                                    fsm.OnStopCompleted();
                                    NotifyStateChanged(task.Name);
                                    return;
                                }
                                slot.Handle = h;
                                // 一次执行只登记一个实例：重试是同一实例的新进程，
                                // 每轮都 OnProcessStarted 会让 ActiveInstances 累加而退出只减一次（S1）。
                                if (attempt == 1) fsm.OnProcessStarted(h.Pid, h.StartedAt);
                                else fsm.OnAttemptStarted(h.Pid, h.StartedAt);
                                NotifyStateChanged(task.Name);
                            }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        TaskLogger.Error(task.Name, "run exception: " + ex);
                        code = -1;
                    }

                    // 用户主动停止（TryStop 已杀进程树）：不得判为失败，也不得触发重试/守护重启。
                    if (slot.StopRequested)
                    {
                        fsm.OnProcessExited(code, true, false, 0, 0, 0);
                        NotifyStateChanged(task.Name);
                        TaskLogger.Info(task.Name, "stopped by user request (exit code " + code + "); not counted as failure");
                        AddRecent(task.Name, "skipped");
                        return; // finally 仍会移除运行槽
                    }

                    lastCode = code;
                    if (code == 0 || attempt > retry) break;
                    TaskLogger.Info(task.Name, "retry " + attempt + "/" + retry + " after non-zero exit " + code);
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
            finally
            {
                fsm.OnProcessExited(lastCode, slot.StopRequested, false, 0, 0, 0);
                NotifyStateChanged(task.Name);
                RemoveSlot(task.Name, slot);
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

        /// <summary>运行槽：存在即"该任务有实例在运行"；Handle 在进程真正启动后回填。</summary>
        private sealed class RunSlot
        {
            public TaskProcessHandle Handle;

            /// <summary>TryStop 置位：终止活实例并取消挂起中的自动重启（等价 systemctl stop 的语义）。</summary>
            public volatile bool StopRequested;
        }

        /// <summary>
        /// detach 任务的守护状态（按任务名）：连续异常失败计数 + 熔断标记。
        /// 预算耗尽标记失败后，只有用户干预（保存重载 / 手动或热键运行 / 重载清空）才能复位；
        /// 实例稳定存活（stableUptimeSec）或成功退出（exit 0）会复位计数。内存态，随宿主会话。
        /// </summary>
        private sealed class SupervisionState
        {
            public int ConsecutiveFailures;
            public bool MarkedFailed;
            public DateTime? MarkedAt;
            public int? LastExitCode;
        }

        private readonly Dictionary<string, SupervisionState> _supervision = new Dictionary<string, SupervisionState>(StringComparer.OrdinalIgnoreCase);

        private void RemoveSlot(string name, RunSlot slot)
        {
            if (slot == null) return;
            lock (_lock)
            {
                List<RunSlot> slots;
                if (!_running.TryGetValue(name, out slots)) return;
                // 只摘这一个实例：allowConcurrent 下同名槽可能有多个，
                // 误摘会让仍在运行的实例失去登记（进而在守护重启校验里被当成"已释放"）。
                if (slots.Remove(slot) && slots.Count == 0) _running.Remove(name);
            }
        }

        /// <summary>
        /// 运行槽登记。allowConcurrent=false 时同名已有在册实例即拒绝；=true 时允许并存，
        /// 但不超过 <see cref="MaxConcurrentInstances"/>。返回 false 时 <paramref name="reason"/>
        /// 给出可写入日志的原因。
        /// </summary>
        private bool TryOccupySlot(string name, bool allowConcurrent, RunSlot slot, out string reason)
        {
            lock (_lock)
            {
                List<RunSlot> slots;
                if (!_running.TryGetValue(name, out slots))
                {
                    slots = new List<RunSlot>();
                    _running[name] = slots;
                }

                if (slots.Count > 0)
                {
                    if (!allowConcurrent)
                    {
                        reason = "concurrent execution not allowed";
                        return false;
                    }
                    if (slots.Count >= MaxConcurrentInstances)
                    {
                        reason = "instance limit reached (" + slots.Count + "/" + MaxConcurrentInstances + " concurrent instances)";
                        return false;
                    }
                }

                slots.Add(slot);
                reason = null;
                return true;
            }
        }

        /// <summary>该任务名下是否仍登记着这个槽：只有真正出册（停止/退出/被替换）才会消失。</summary>
        private bool IsSlotRegistered(string name, RunSlot slot)
        {
            if (slot == null) return false;
            lock (_lock)
            {
                List<RunSlot> slots;
                return _running.TryGetValue(name, out slots) && slots.Contains(slot);
            }
        }

        private void AttachDetached(TaskDefinition task, RunSlot slot, TaskProcessHandle handle)
        {
            // 启动窗口期停止竞态（H2）：TryStop 可能在 StartDetached 与本调用之间摘除槽并置位
            // StopRequested——刚 spawn 的常驻进程必须立即终止，不得登记（否则脱离注册表不可停）。
            if (slot.StopRequested)
            {
                TaskLogger.Info(task.Name, "detached process " + handle.Pid + " spawned after user stop request; terminating immediately");
                try { handle.Stop(); } catch { }
                RemoveSlot(task.Name, slot);
                GetOrCreateFsm(task.Name).OnStopCompleted();
                NotifyStateChanged(task.Name);
                AddRecent(task.Name, "stopped");
                return;
            }

            slot.Handle = handle;
            var fsm = GetOrCreateFsm(task.Name);
            fsm.OnProcessStarted(handle.Pid, handle.StartedAt);
            NotifyStateChanged(task.Name);
            AddRecent(task.Name, "started");
            TaskLogger.Info(task.Name, "detached: running in background pid=" + handle.Pid + " killWithHost=" + handle.KillWithHost);
            handle.Exited += (h, exitCode) => OnDetachedExited(task, slot, h, exitCode);
            handle.BeginExitWatch();
        }

        /// <summary>
        /// detach 实例退出（后台 watcher 线程）：清运行槽 + recent + 意外退出通知/守护重启决策。
        /// 被主动停止（用户/宿主退出清理）不算失败，不触发通知、不参与重启。
        /// </summary>
        private void OnDetachedExited(TaskDefinition task, RunSlot slot, TaskProcessHandle handle, int? exitCode)
        {
            try
            {
                var fsm = GetOrCreateFsm(task.Name);
                if (handle.WasStopped || slot.StopRequested)
                {
                    RemoveSlot(task.Name, slot);
                    fsm.OnProcessExited(exitCode ?? 0, true, false, 0, 0, 0);
                    NotifyStateChanged(task.Name);
                    AddRecent(task.Name, "stopped");
                    return;
                }

                // 稳定存活复位（pm2 min_uptime / supervisord startsecs 语义）：
                // 存活达 stableUptimeSec 的实例退出时重置连续失败计数，长稳后的偶发崩溃不被历史连坐。
                // 0 = 计数永不自动复位。
                int stable = task.Options != null ? task.Options.StableUptimeSec : 60;
                double uptimeSec = (DateTime.Now - handle.StartedAt).TotalSeconds;
                if (stable > 0 && uptimeSec >= stable)
                {
                    lock (_lock) _supervision.Remove(task.Name);
                }

                if (exitCode.HasValue && exitCode.Value == 0)
                {
                    // 成功退出同样复位计数：计数只反映"连续的异常失败"
                    lock (_lock) _supervision.Remove(task.Name);
                    RemoveSlot(task.Name, slot);
                    fsm.OnProcessExited(0, false, false, 0, 0, stable);
                    NotifyStateChanged(task.Name);
                    AddRecent(task.Name, "ok");
                    return;
                }

                if (task.Options != null && task.Options.RestartOnFailure)
                {
                    // 守护路径：重启延迟期内槽保持占用，防止触发器在窗口期并发拉起第二个实例
                    HandleDetachFailure(task, slot, "exit", exitCode ?? -1);
                    return;
                }

                RemoveSlot(task.Name, slot);
                fsm.OnProcessExited(exitCode ?? -1, false, false, 0, 0, stable);
                NotifyStateChanged(task.Name);
                AddRecent(task.Name, exitCode.HasValue ? "fail:" + exitCode.Value : "fail");
                RecordFailureNotify(task, exitCode ?? -1);
            }
            catch { }
        }

        /// <summary>
        /// 守护决策：异常失败计数 → 预算内则延迟重启，耗尽则熔断标记失败。
        /// 熔断参考 systemd StartLimitBurst（进入 failed 态等人工复位）与 pm2 max_restarts（errored 态）。
        /// </summary>
        private void HandleDetachFailure(TaskDefinition task, RunSlot slot, string what, int exitCode)
        {
            // 槽保留（重启延迟期内占住防重位），但活句柄清空：
            // TryStop 依此区分"杀实例"与"取消挂起重启"，GetRunning 也不会再报已死的 pid
            slot.Handle = null;
            var fsm = GetOrCreateFsm(task.Name);
            int stable = task.Options != null ? task.Options.StableUptimeSec : 60;
            int limit = task.Options != null ? task.Options.RestartLimit : 3;
            int delay = task.Options != null ? task.Options.RestartDelaySec : 5;

            var newState = fsm.OnProcessExited(exitCode, false, true, delay, limit, stable);
            NotifyStateChanged(task.Name);

            int failures;
            lock (_lock)
            {
                SupervisionState sup;
                if (!_supervision.TryGetValue(task.Name, out sup))
                {
                    sup = new SupervisionState();
                    _supervision[task.Name] = sup;
                }
                sup.ConsecutiveFailures = fsm.ConsecutiveFailures;
                sup.LastExitCode = exitCode;
                failures = sup.ConsecutiveFailures;
            }

            if (newState == TaskRuntimeState.MarkedFailed)
            {
                lock (_lock)
                {
                    SupervisionState sup;
                    if (!_supervision.TryGetValue(task.Name, out sup))
                    {
                        // 熔断时必须落标记并继续 RemoveSlot：此前的提前 return 会让槽永久滞留
                        // 在 _running（非并发任务从此每次触发都被拒），任务静默瘫痪（M5）。
                        // 两段临界区之间并发 ApplyCoreLocked 清空 _supervision 的窗口内，
                        // 这里兜底重建条目而不是放弃收尾。
                        sup = new SupervisionState();
                        _supervision[task.Name] = sup;
                    }
                    // 最近事件、槽位收尾与熔断标记放进同一临界区（M8）：
                    // 外部读取 GetSupervision()/GetRecent() 时不会再看到
                    // “已标记熔断但最近事件里还没有 marked”的中间态。
                    // AddRecent/RemoveSlot 内部同样 lock (_lock)，Monitor 可重入。
                    AddRecent(task.Name, "marked");
                    RemoveSlot(task.Name, slot);
                    sup.MarkedFailed = true;
                    sup.MarkedAt = DateTime.Now;
                }
                TaskLogger.Error(task.Name, "supervise: marked FAILED after " + failures + " consecutive failures (last " +
                    what + " exit=" + exitCode + "); auto-restart disabled until task is saved/reloaded or manually/hotkey run");
                NotifyMarkedFailed(task, failures);
                return;
            }

            TaskLogger.Warn(task.Name, "supervise: " + what + " failure #" + failures + "/" + (limit + 1) +
                " exit=" + exitCode + ", restart in " + delay + "s");
            var t = task;
            var s = slot;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, delay))).ConfigureAwait(false);
                    RestartContinue(t, s);
                }
                catch (Exception ex)
                {
                    try { TaskLogger.Error(t.Name, "supervise restart error: " + ex); } catch { }
                }
            });
        }

        /// <summary>重启延续：校验槽/任务/调度器仍然有效后重新拉起；启动失败递归回守护决策直至预算耗尽。</summary>
        private void RestartContinue(TaskDefinition task, RunSlot slot)
        {
            var fsm = GetOrCreateFsm(task.Name);
            try
            {
                // 槽已不在册 = 用户停止 / 宿主退出 / 配置重载替换过：放弃重启
                if (!IsSlotRegistered(task.Name, slot))
                {
                    // 状态机可能停在 WaitingRetry（倒计时已结束但重启被外部取消）：
                    // 不归位会让任务永久卡在"等待重试"（H4）。
                    fsm.OnAbandon();
                    NotifyStateChanged(task.Name);
                    TaskLogger.Info(task.Name, "supervise: restart cancelled (slot released)");
                    return;
                }
                if (!_started || !_globalEnabled || !IsTaskStillEnabled(task) || slot.StopRequested)
                {
                    RemoveSlot(task.Name, slot);
                    fsm.OnStopCompleted();
                    NotifyStateChanged(task.Name);
                    TaskLogger.Info(task.Name, "supervise: restart cancelled (task disabled/stopped or scheduler stopped)");
                    return;
                }

                TaskProcessHandle handle;
                int code = TaskRunner.StartDetached(task, "supervise", out handle);
                if (code == TaskRunner.StartSkippedSingleInstance)
                {
                    RemoveSlot(task.Name, slot);
                    // 放弃启动不走 OnProcessExited(0)：那会清零刚累计的连续失败计数（M1）
                    fsm.OnAbandon();
                    NotifyStateChanged(task.Name);
                    AddRecent(task.Name, "skipped");
                    return;
                }
                if (code != 0 || handle == null)
                {
                    HandleDetachFailure(task, slot, "start", -1);
                    return;
                }
                AttachDetached(task, slot, handle);
            }
            catch (Exception ex)
            {
                TaskLogger.Error(task.Name, "supervise restart exception: " + ex);
                RemoveSlot(task.Name, slot);
                // 异常分支同样要归位：否则 FSM 停在 WaitingRetry，后续触发全被拒绝（H4）
                fsm.OnAbandon();
                NotifyStateChanged(task.Name);
                AddRecent(task.Name, "fail");
            }
        }

        private void NotifyMarkedFailed(TaskDefinition task, int failures)
        {
            try
            {
                if (task.Options != null && !task.Options.NotifyOnFailure) return;
                Notify(Loc.T("Tasks.MarkedFailedNotify",
                    "任务 [{0}] 连续异常退出 {1} 次，已标记失败并停止自动重启。\n修复后重新保存任务，或手动/热键运行即可重置。",
                    task.Name, failures));
            }
            catch { }
        }

        public IReadOnlyList<TaskRunInfo> GetRunning()
        {
            var list = new List<TaskRunInfo>();
            lock (_lock)
            {
                //逐实例列出：allowConcurrent 下同名任务会有多条记录（此前只报最后一个）
                foreach (var slots in _running.Values)
                {
                    foreach (var s in slots)
                    {
                        var h = s != null ? s.Handle : null;
                        if (h == null) continue;
                        list.Add(new TaskRunInfo
                        {
                            Name = h.Definition != null ? h.Definition.Name : "",
                            Pid = h.Pid,
                            StartedAt = h.StartedAt,
                            KillWithHost = h.KillWithHost
                        });
                    }
                }
            }
            return list.AsReadOnly();
        }

        public bool IsRunning(string taskName)
        {
            if (string.IsNullOrEmpty(taskName)) return false;
            lock (_lock)
            {
                List<RunSlot> slots;
                return _running.TryGetValue(taskName, out slots) && slots.Count > 0;
            }
        }

        /// <summary>
        /// 停止该任务当前在册的**全部**实例（allowConcurrent 下可能不止一个）。
        /// 用户点一次"停止任务"的语义是"这个任务停下来"，不是"停掉其中一个"。
        /// 同时对每个槽置StopRequested，取消各自挂起中的自动重启（systemctl stop 语义）。
        ///
        /// 杀进程（taskkill /T 每实例最长等 5s）转后台执行（H3）：本方法从 UI 事件
        /// （管理窗口停止/删除/重启按钮、托盘"点击停止"、编辑器停止）同步调用，
        /// 进程树顽固时同步 taskkill 会冻结界面数秒；状态置位与槽出册在本线程完成后，
        /// 句柄终止与 FSM 收尾交给线程池，UI 立即得到"已受理"。返回值语义保持
        /// "是否受理了停止请求"（与既有调用方判断一致），不代表进程已终结。
        /// </summary>
        public bool TryStop(string taskName)
        {
            List<TaskProcessHandle> toKill;
            TaskStateMachine fsm;
            bool accepted = StopInternal(taskName, out toKill, out fsm);

            if (toKill != null && toKill.Count > 0)
            {
                var name = taskName;
                var fsmToFinish = fsm;
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    var okAll = true;
                    foreach (var h in toKill)
                    {
                        try { h.Stop(); }
                        catch { okAll = false; }
                    }
                    fsmToFinish.OnStopCompleted();
                    NotifyStateChanged(name);
                    if (!okAll)
                    {
                        try { TaskLogger.Warn(name, "stop: one or more process handles failed to terminate"); } catch { }
                    }
                });
            }
            return accepted;
        }

        /// <summary>
        /// 停止并等待该任务的全部实例真正退出（H3）。重启前必须走这条路径：
        /// 槽位出册与进程终结都确认后才允许启动新实例。返回 false 表示超时仍有进程存活。
        /// </summary>
        private async Task<bool> StopAndWaitAsync(string taskName, TimeSpan timeout)
        {
            List<TaskProcessHandle> toKill;
            TaskStateMachine fsm;
            StopInternal(taskName, out toKill, out fsm);

            // 无活进程（未运行 / 仅取消了挂起重启）：StopInternal 已出册并复位，无需等待。
            if (toKill == null || toKill.Count == 0) return true;

            bool allExited = await Task.Run(() =>
            {
                var ok = true;
                foreach (var h in toKill)
                {
                    try { h.Stop(); }
                    catch { ok = false; }
                }
                foreach (var h in toKill)
                {
                    if (!h.WaitForExit(timeout)) ok = false;
                }
                return ok;
            }).ConfigureAwait(false);

            fsm.OnStopCompleted();
            NotifyStateChanged(taskName);
            if (!allExited)
            {
                TaskLogger.Error(taskName, "stop timed out after " + timeout.TotalSeconds + "s; instances may still be running");
            }
            return allExited;
        }

        /// <summary>
        /// 停止请求的公共前半段：状态机置位、槽位出册、返回需要终止的句柄。
        /// 返回 false 表示没有任何可停止的实例/挂起重启（与 <see cref="TryStop"/> 的返回语义一致）。
        /// </summary>
        private bool StopInternal(string taskName, out List<TaskProcessHandle> toKill, out TaskStateMachine fsm)
        {
            toKill = null;
            fsm = null;
            if (string.IsNullOrEmpty(taskName)) return false;

            fsm = GetOrCreateFsm(taskName);
            bool cancelledPendingRetry;
            bool stopRequested = fsm.RequestStop(out cancelledPendingRetry);
            NotifyStateChanged(taskName);

            List<TaskProcessHandle> handles = null;
            lock (_lock)
            {
                List<RunSlot> slots;
                if (_running.TryGetValue(taskName, out slots) && slots.Count > 0)
                {
                    handles = new List<TaskProcessHandle>();
                    // 倒序遍历：延迟期内无活进程的槽要就地摘除，正序会破坏迭代器
                    for (int i = slots.Count - 1; i >= 0; i--)
                    {
                        var s = slots[i];
                        if (s == null) { slots.RemoveAt(i); continue; }
                        s.StopRequested = true;
                        var h = s.Handle;
                        if (h == null)
                        {
                            // 重启延迟期/尚未启动：没有活进程可杀，取消挂起重启即可，立即出册
                            slots.RemoveAt(i);
                            continue;
                        }
                        handles.Add(h);
                    }
                    if (slots.Count == 0) _running.Remove(taskName);
                }
            }

            if (cancelledPendingRetry || handles == null || handles.Count == 0)
            {
                fsm.OnStopCompleted();
                NotifyStateChanged(taskName);
                return cancelledPendingRetry || stopRequested;
            }

            toKill = handles;
            return true;
        }

        /// <summary>detach 任务的守护状态快照：连续失败计数与熔断标记（供 UI 展示"已标记失败"）。</summary>
        public TaskSupervisionInfo GetSupervision(string taskName)
        {
            var info = new TaskSupervisionInfo();
            if (string.IsNullOrEmpty(taskName)) return info;
            lock (_lock)
            {
                SupervisionState s;
                if (_supervision.TryGetValue(taskName, out s))
                {
                    info.MarkedFailed = s.MarkedFailed;
                    info.ConsecutiveFailures = s.ConsecutiveFailures;
                    info.LastExitCode = s.LastExitCode;
                    info.MarkedAt = s.MarkedAt;
                }
            }
            return info;
        }

        /// <summary>宿主/调度器退出：killWithHost=true 的实例整树终止；false 的解除跟踪、进程继续存活。</summary>
        private void StopRunningInstances()
        {
            List<TaskProcessHandle> handles;
            lock (_lock)
            {
                handles = new List<TaskProcessHandle>();
                // 逐实例遍历（allowConcurrent 下同名任务可能有多条在册槽）
                foreach (var slots in _running.Values)
                {
                    foreach (var s in slots)
                    {
                        if (s == null) continue;
                        // 宿主退出对所有实例都是"主动停止"：先置位 StopRequested，
                        // 否则 killWithHost=false 的常驻任务在 h.Dispose() 之后，
                        // 仍在等待的 exit watcher 只能拿到异常退出码，会被判定为意外退出
                        // 而弹一次虚假"失败"通知（Dispose 使 WaitForExit 抛异常 → 返回 -1）。
                        s.StopRequested = true;
                        if (s.Handle != null) handles.Add(s.Handle);
                    }
                }
                _running.Clear();
            }
            foreach (var h in handles)
            {
                try
                {
                    if (h.KillWithHostRequested)
                    {
                        if (!h.KillWithHost)
                        {
                            // 作业对象挂接失败（KillWithHost=false）但用户仍要求随宿主退出：走 taskkill 兜底
                            TaskLogger.Warn(h.Definition.Name, "host exiting, killing process tree via taskkill (pid=" + h.Pid + ", job attach had failed)");
                        }
                        h.Stop();
                    }
                    else TaskLogger.Info(h.Definition.Name, "host exiting, detached process left running (pid=" + h.Pid + ", killWithHost=false)");
                    h.Dispose();
                }
                catch { }
            }
        }

        private bool IsTaskStillEnabled(TaskDefinition task)
        {
            if (task == null) return false;
            lock (_lock)
            {
                if (!_started || !_globalEnabled) return false;

                // 按任务名判定，而不是引用相等。
                // 每次 ApplyCoreLocked 都整体替换 _tasks，而元素来自 ParseTaskNode —— 每次解析都 new
                // TaskDefinition()。因此任何一次配置重载（托盘"重载任务"、编辑器"保存并重载"、
                // ReloadConfig()）都会让存量 TaskDefinition 引用全部失效：
                //   - 用 ReferenceEquals 时，"重载"会被误判成"任务被删除"，detach 守护链在重启延续
                //     阶段被静默取消，常驻任务永久失去守护，且日志原因误导用户（"retry cancelled
                //     because task was disabled, removed, or scheduler stopped"）；
                //   - wait 模式的重试循环同样会在这里提前 return。
                var current = _tasks.FirstOrDefault(t =>
                    string.Equals(t.Name, task.Name, StringComparison.OrdinalIgnoreCase));
                return current != null && current.Enabled;
            }
        }

        private List<string> _recent = new List<string>();
        private void AddRecent(string name, string status)
        {
            // watcher 线程与执行线程都会写入；Monitor 可重入，调用点的既有 lock 不受影响
            lock (_lock)
            {
                string entry = string.Format("{0:HH:mm:ss} {1} {2}", DateTime.Now, name, status);
                _recent.Insert(0, entry);
                if (_recent.Count > 10) _recent.RemoveAt(10);
            }
        }

        private void AddRecent(string name, int code)
        {
            AddRecent(name, code == 0 ? "ok" : "fail:" + code);
        }
        public IReadOnlyList<string> GetRecent()
        {
            // 必须返回快照：AsReadOnly() 只是包装原 list 的活视图，锁在返回瞬间即释放。
            // watcher 线程持续 Insert(0, entry) 时，消费方 foreach 会抛"集合已修改"，
            // 被 ModuleManager.GetItemsGuarded catch 后整个托盘 Tasks 菜单整块消失。
            lock (_lock) return _recent.ToArray();
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;

            // SystemEvents 在专用线程上触发本回调，而 DispatcherTimer 必须在已启动消息泵的
            // UI 线程上创建：在 SystemEvents 线程上 new DispatcherTimer 会隐式创建该线程的
            // Dispatcher（没有 Run 循环），Tick 永远不会触发。
            var dispatcher = _dispatcher;
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
