using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CarroDesk.Common;
using Newtonsoft.Json;

namespace CarroDesk.Services.Tasks
{
    /// <summary>
    /// 触发策略值。由调用方从任务配置构造，本类刻意不直接依赖 <c>TaskOptions</c>：
    /// 一是限流核心可独立编译/单测，二是同一套策略可服务于任意触发器类型。
    /// 见 docs/TASK-FIRE-LIMITER-DESIGN-20261114.md §6。
    /// </summary>
    public sealed class FireLimitPolicy
    {
        /// <summary>每天最多触发次数，0 = 不限（本地自然日 00:00 归零）。</summary>
        public int MaxRunsPerDay { get; set; }

        /// <summary>两次触发最短间隔秒数，0 = 不限。</summary>
        public int MinIntervalSec { get; set; }

        public bool IsUnlimited
        {
            get { return MaxRunsPerDay <= 0 && MinIntervalSec <= 0; }
        }

        public static FireLimitPolicy Unlimited
        {
            get { return new FireLimitPolicy(); }
        }
    }

    /// <summary>限流状态快照（供 UI / 排障 / 单测读取）。</summary>
    public sealed class FireLimitSnapshot
    {
        public string TaskName { get; set; } = "";
        public string Date { get; set; } = "";
        public int Count { get; set; }
        public DateTime? LastFireUtc { get; set; }
        public DateTime? NextAllowedUtc { get; set; }
    }

    internal sealed class FireLimitState
    {
        public string Date { get; set; } = "";
        public int Count { get; set; }

        /// <summary>UTC ticks。用整数而非 DateTime 落盘，避免反序列化后的 Kind 歧义。</summary>
        public long LastFireUtcTicks { get; set; }
    }

    internal sealed class FireLimitStateFile
    {
        public int Version { get; set; }
        public Dictionary<string, FireLimitState> Tasks { get; set; }
    }

    /// <summary>
    /// 任务级触发频率限制：每日次数上限 + 两次触发最短间隔。
    ///
    /// 为什么状态放在这个类而不是触发器里：TaskSchedulerService.ApplyCoreLocked 每次
    /// 配置重载都会 StopTriggers 并整体重建触发器实例，触发器内的任何字段都会清零，
    /// 用户保存一次任务即可绕过限流。本类由服务层单例持有，且跨宿主重启落盘。
    ///
    /// 持久化范式沿用 PinGuard（Services/PinService.cs）：延迟读回、AtomicFile 原子写、
    /// 写失败降级为进程内计数并留痕，绝不因限流自身故障阻断任务执行。
    /// </summary>
    public sealed class TaskFireLimiter
    {
        private const int StateVersion = 1;

        public static string DefaultStatePath
        {
            get { return Path.Combine(ConfigService.DirPath, "task-fire-state.json"); }
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, FireLimitState> _states =
            new Dictionary<string, FireLimitState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>null = 完全不落盘（单测/内存态注入）。</summary>
        private readonly string _statePath;
        private readonly Func<DateTime> _utcClock;
        private readonly Func<DateTime> _localClock;
        private bool _stateLoaded;

        public TaskFireLimiter(string statePath = null, Func<DateTime> utcClock = null, Func<DateTime> localClock = null)
        {
            _statePath = statePath;
            _utcClock = utcClock ?? (() => DateTime.UtcNow);
            _localClock = localClock ?? (() => DateTime.Now);
        }

        /// <summary>
        /// 人工类触发的 reason 判定。注意 HotkeyTrigger 发出的是 "hotkey:&lt;组合键&gt;"，
        /// 因此必须用前缀而非全等匹配（既有 ExecuteAsync 的精确匹配偏差见设计文档附录 B）。
        /// 本方法只是工具：是否绕过由服务层按 limitScope 决定，limiter 自身不做业务分流。
        /// </summary>
        public static bool IsUserDrivenReason(string reason)
        {
            // 统一到 TriggerReason，避免限流门、ExecuteAsync、OnTriggerFired 各写一套前缀规则
            return TriggerReason.IsUserDriven(reason);
        }

        /// <summary>
        /// 只读准入判定：不改变计数、不落盘。
        /// 供 IdleTrigger 在置位 _fired 之前调用——被拒绝时保持可重试，冷却/额度恢复后
        /// 下一次 IdleTick 自然补触发；真正的消耗仍统一由服务层的 TryAcquire 完成，
        /// 因此不存在重复计数。
        /// </summary>
        public bool CanFire(string taskName, FireLimitPolicy policy, out string refusal)
        {
            lock (_sync)
            {
                EnsureStateLoadedNoLock();
                if (policy == null || policy.IsUnlimited) { refusal = null; return true; }

                FireLimitState state;
                _states.TryGetValue(Key(taskName), out state);
                return EvaluateNoLock(state, policy, _utcClock(), TodayKey(), out refusal);
            }
        }

        /// <summary>
        /// 原子「检查并消耗」一次配额。在一把锁内完成日期滚动、间隔检查、次数检查、
        /// 计数递增、落盘；并发触发不会同时读到同一个 Count 而互相覆盖。
        /// true = 放行；false = 已被限流，refusal 为可直接写日志的英文原因。
        /// </summary>
        public bool TryAcquire(string taskName, FireLimitPolicy policy, string reason, out string refusal)
        {
            lock (_sync)
            {
                EnsureStateLoadedNoLock();
                // 默认配置零开销：不创建状态条目、不落盘、不写日志。
                if (policy == null || policy.IsUnlimited) { refusal = null; return true; }

                string key = Key(taskName);
                DateTime nowUtc = _utcClock();
                string today = TodayKey();

                FireLimitState state;
                _states.TryGetValue(key, out state);

                // 系统时间回拨：UTC 倒退时若继续按 LastFireUtc 判定，最短间隔会永久不满足。
                // 重置该任务状态并放行（记 WARN），保守但不把用户锁死。
                if (state != null && state.LastFireUtcTicks > 0 &&
                    nowUtc < new DateTime(state.LastFireUtcTicks, DateTimeKind.Utc))
                {
                    _states.Remove(key);
                    state = null;
                    TaskLogger.Warn(taskName ?? "system", "fire limit: system clock rolled back, quota state reset");
                    PersistNoLock();
                }

                if (!EvaluateNoLock(state, policy, nowUtc, today, out refusal)) return false;

                if (state == null)
                {
                    state = new FireLimitState();
                    _states[key] = state;
                }
                if (!string.Equals(state.Date, today, StringComparison.Ordinal))
                {
                    state.Date = today;
                    state.Count = 0;
                }
                state.Count++;
                state.LastFireUtcTicks = nowUtc.Ticks;
                PersistNoLock();
                return true;
            }
        }

        /// <summary>清理配置中已删除任务的计数条目，防止字典随历史任务名无界增长。</summary>
        public void ForgetRemovedTasks(IEnumerable<string> activeNames)
        {
            var active = new HashSet<string>(activeNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            lock (_sync)
            {
                EnsureStateLoadedNoLock();
                var gone = _states.Keys.Where(k => !active.Contains(k)).ToList();
                if (gone.Count == 0) return;
                foreach (var k in gone) _states.Remove(k);
                PersistNoLock();
            }
        }

        public FireLimitSnapshot GetSnapshot(string taskName, FireLimitPolicy policy = null)
        {
            lock (_sync)
            {
                EnsureStateLoadedNoLock();
                FireLimitState state;
                _states.TryGetValue(Key(taskName), out state);
                var snap = new FireLimitSnapshot { TaskName = taskName ?? "" };
                if (state == null) return snap;

                snap.Date = state.Date ?? "";
                snap.Count = state.Count;
                if (state.LastFireUtcTicks > 0)
                {
                    var last = new DateTime(state.LastFireUtcTicks, DateTimeKind.Utc);
                    snap.LastFireUtc = last;
                    if (policy != null && policy.MinIntervalSec > 0)
                        snap.NextAllowedUtc = last.AddSeconds(policy.MinIntervalSec);
                }
                return snap;
            }
        }

        private static bool EvaluateNoLock(FireLimitState state, FireLimitPolicy policy,
            DateTime nowUtc, string today, out string refusal)
        {
            refusal = null;

            if (state != null && state.LastFireUtcTicks > 0)
            {
                DateTime last = new DateTime(state.LastFireUtcTicks, DateTimeKind.Utc);
                // 回拨：只读路径直接放行，写入路径会在调用方先重置状态。
                if (nowUtc < last) return true;

                if (policy.MinIntervalSec > 0)
                {
                    TimeSpan required = TimeSpan.FromSeconds(policy.MinIntervalSec);
                    TimeSpan elapsed = nowUtc - last;
                    if (elapsed < required)
                    {
                        refusal = "min interval " + policy.MinIntervalSec + "s not elapsed, " +
                                  Math.Ceiling((required - elapsed).TotalSeconds) + "s left";
                        return false;
                    }
                }
            }

            if (policy.MaxRunsPerDay > 0)
            {
                int count = (state != null && string.Equals(state.Date, today, StringComparison.Ordinal))
                    ? state.Count : 0;
                if (count >= policy.MaxRunsPerDay)
                {
                    refusal = "daily quota " + count + "/" + policy.MaxRunsPerDay +
                              " reached (resets at local midnight)";
                    return false;
                }
            }

            return true;
        }

        private string Key(string taskName)
        {
            return string.IsNullOrWhiteSpace(taskName) ? "?" : taskName.Trim();
        }

        private string TodayKey()
        {
            return _localClock().ToString("yyyy-MM-dd");
        }

        private void EnsureStateLoadedNoLock()
        {
            if (_stateLoaded) return;
            _stateLoaded = true;
            if (string.IsNullOrEmpty(_statePath)) return;

            try
            {
                if (!File.Exists(_statePath)) return;
                string json = File.ReadAllText(_statePath, Encoding.UTF8);
                var file = JsonConvert.DeserializeObject<FireLimitStateFile>(json);
                if (file == null || file.Version != StateVersion || file.Tasks == null)
                {
                    RecoverFromInvalidStateNoLock("unsupported version or empty envelope");
                    return;
                }

                foreach (var kv in file.Tasks)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
                    if (kv.Value.Count < 0 || kv.Value.LastFireUtcTicks < 0) continue;
                    _states[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                RecoverFromInvalidStateNoLock(ex.Message);
            }
        }

        /// <summary>
        /// 状态文件损坏/版本不识别：备份原件、清空计数、立即回写一份合法空状态。
        /// 与 PinGuard 一样，绝不因状态文件问题抛异常阻断任务；但必须留痕，
        /// 不能静默把「用户配置的每天 3 次」变成「无限次」。
        /// </summary>
        private void RecoverFromInvalidStateNoLock(string detail)
        {
            try
            {
                string bak = _statePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(_statePath, bak, true);
                TaskLogger.Warn("system", "fire limit state backed up to " + bak + ": " + detail);
            }
            catch { }

            _states.Clear();
            TaskLogger.Warn("system", "fire limit state unreadable (" + detail + "), counters reset");
            PersistNoLock();
        }

        private void PersistNoLock()
        {
            if (string.IsNullOrEmpty(_statePath)) return;
            try
            {
                var file = new FireLimitStateFile
                {
                    Version = StateVersion,
                    Tasks = new Dictionary<string, FireLimitState>(_states, StringComparer.OrdinalIgnoreCase)
                };
                AtomicFile.WriteAllText(_statePath, JsonConvert.SerializeObject(file, Formatting.Indented), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // 落盘失败：降级为进程内计数，本次判定照常生效，但必须留痕。
                TaskLogger.Warn("system", "fire limit state persist failed, in-memory counters still active: " + ex.Message);
            }
        }
    }
}
