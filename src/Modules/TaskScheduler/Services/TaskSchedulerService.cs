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
        private Dictionary<string, RunSlot> _running = new Dictionary<string, RunSlot>(StringComparer.OrdinalIgnoreCase);
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
            lock (_lock)
            {
                _tasks = result.Tasks ?? new List<TaskDefinition>();
                // 配置变化 = 用户干预信号：清空守护计数与失败标记（同"保存任务即可重置"的语义）
                _supervision.Clear();
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

            // 占槽即防重：槽在进程真正启动前就存在，启动窗口期内的并发触发不会漏判。
            // wait 模式在 finally 清槽；detach 模式的槽由退出 watcher 清理。
            RunSlot slot = new RunSlot();
            bool skip = false;
            lock (_lock)
            {
                if (_running.ContainsKey(task.Name))
                {
                    bool allow = task.Options != null && task.Options.AllowConcurrent;
                    if (!allow) skip = true;
                }
                if (!skip) _running[task.Name] = slot;
            }
            if (skip)
            {
                TaskLogger.Warn(task.Name, "skipped(" + reason + ") concurrent execution not allowed");
                return;
            }

            // detach（常驻）模式：启动即返回，不等退出；配置校验已拒绝 detach+retry/timeout 组合
            if (task.Options != null && task.Options.IsDetach)
            {
                // 熔断标记后：自动触发路径一律跳过，等待用户干预；手动/热键运行视为显式复位意图
                SupervisionState sup = GetSupervisionState(task.Name);
                bool userDriven = string.Equals(reason, "manual", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(reason, "hotkey", StringComparison.OrdinalIgnoreCase);
                if (sup != null && sup.MarkedFailed)
                {
                    if (!userDriven)
                    {
                        RemoveSlot(task.Name, slot);
                        TaskLogger.Warn(task.Name, "skipped(" + reason + ") marked failed after " + sup.ConsecutiveFailures +
                            " consecutive failures; reset by editing+saving the task or a manual/hotkey run");
                        return;
                    }
                    lock (_lock) _supervision.Remove(task.Name);
                    TaskLogger.Info(task.Name, "failed mark reset by " + reason + " run");
                }

                TaskProcessHandle handle;
                int code = TaskRunner.StartDetached(task, reason, out handle);
                if (code == TaskRunner.StartSkippedSingleInstance)
                {
                    // 单实例互斥：已有实例在运行（含跨宿主重启的旧实例），不算失败、不计数
                    RemoveSlot(task.Name, slot);
                    AddRecent(task.Name, "skipped");
                    return;
                }
                if (code != 0 || handle == null)
                {
                    if (task.Options.RestartOnFailure)
                    {
                        // 启动失败同样进入守护决策（坏路径也是 crash loop 的一种）
                        HandleDetachFailure(task, slot, "start", -1);
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
                            h => slot.Handle = h).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        TaskLogger.Error(task.Name, "run exception: " + ex);
                        code = -1;
                    }

                    // 用户主动停止（TryStop 已杀进程树）：不得判为失败，也不得触发重试/守护重启。
                    if (slot.StopRequested)
                    {
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
            lock (_lock)
            {
                RunSlot cur;
                if (_running.TryGetValue(name, out cur) && ReferenceEquals(cur, slot)) _running.Remove(name);
            }
        }

        private void AttachDetached(TaskDefinition task, RunSlot slot, TaskProcessHandle handle)
        {
            slot.Handle = handle;
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
                if (handle.WasStopped || slot.StopRequested)
                {
                    RemoveSlot(task.Name, slot);
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
            int failures;
            lock (_lock)
            {
                SupervisionState sup;
                if (!_supervision.TryGetValue(task.Name, out sup))
                {
                    sup = new SupervisionState();
                    _supervision[task.Name] = sup;
                }
                sup.ConsecutiveFailures++;
                sup.LastExitCode = exitCode;
                failures = sup.ConsecutiveFailures;
            }

            int limit = task.Options != null ? task.Options.RestartLimit : 3;
            int delay = task.Options != null ? task.Options.RestartDelaySec : 5;

            if (failures > limit)
            {
                bool notify;
                lock (_lock)
                {
                    SupervisionState sup;
                    if (!_supervision.TryGetValue(task.Name, out sup)) return;
                    sup.MarkedFailed = true;
                    sup.MarkedAt = DateTime.Now;
                    notify = true;
                }
                RemoveSlot(task.Name, slot);
                TaskLogger.Error(task.Name, "supervise: marked FAILED after " + failures + " consecutive failures (last " +
                    what + " exit=" + exitCode + "); auto-restart disabled until task is saved/reloaded or manually/hotkey run");
                AddRecent(task.Name, "marked");
                if (notify) NotifyMarkedFailed(task, failures);
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
            try
            {
                // 槽已不在册 = 用户停止 / 宿主退出 / 配置重载替换过：放弃重启
                lock (_lock)
                {
                    RunSlot cur;
                    if (!_running.TryGetValue(task.Name, out cur) || !ReferenceEquals(cur, slot))
                    {
                        TaskLogger.Info(task.Name, "supervise: restart cancelled (slot released)");
                        return;
                    }
                }
                if (!_started || !_globalEnabled || !IsTaskStillEnabled(task) || slot.StopRequested)
                {
                    RemoveSlot(task.Name, slot);
                    TaskLogger.Info(task.Name, "supervise: restart cancelled (task disabled/stopped or scheduler stopped)");
                    return;
                }

                TaskProcessHandle handle;
                int code = TaskRunner.StartDetached(task, "supervise", out handle);
                if (code == TaskRunner.StartSkippedSingleInstance)
                {
                    RemoveSlot(task.Name, slot);
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

        private SupervisionState GetSupervisionState(string taskName)
        {
            lock (_lock)
            {
                SupervisionState s;
                return _supervision.TryGetValue(taskName ?? "", out s) ? s : null;
            }
        }

        public IReadOnlyList<TaskRunInfo> GetRunning()
        {
            var list = new List<TaskRunInfo>();
            lock (_lock)
            {
                foreach (var s in _running.Values)
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
            return list.AsReadOnly();
        }

        public bool IsRunning(string taskName)
        {
            if (string.IsNullOrEmpty(taskName)) return false;
            lock (_lock) return _running.ContainsKey(taskName);
        }

        public bool TryStop(string taskName)
        {
            if (string.IsNullOrEmpty(taskName)) return false;
            TaskProcessHandle handle;
            lock (_lock)
            {
                RunSlot s;
                if (!_running.TryGetValue(taskName, out s)) return false;
                if (s != null) s.StopRequested = true; // 同时取消挂起中的自动重启（systemctl stop 语义）
                handle = s != null ? s.Handle : null;
                if (handle == null)
                {
                    // 重启延迟期内没有活进程：取消挂起重启即可，立即出册
                    _running.Remove(taskName);
                }
            }
            if (handle == null) return true;
            try
            {
                handle.Stop();
                return true;
            }
            catch
            {
                return false;
            }
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
                foreach (var s in _running.Values)
                {
                    if (s != null && s.Handle != null) handles.Add(s.Handle);
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
                return _started && _globalEnabled && _tasks.Any(t =>
                    ReferenceEquals(t, task) && t.Enabled);
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
