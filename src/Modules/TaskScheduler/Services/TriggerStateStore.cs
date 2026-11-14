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
    /// 按「任务名 + 键」保存触发器状态（last-fired 等）。
    ///
    /// 为什么需要：ApplyCoreLocked 每次配置重载都会 StopTriggers 并重建全部触发器实例，
    /// 任何实例字段（DailyTrigger._lastFiredDate、CronTrigger._lastFiredMinute ...）都会清零。
    /// 服务层此前只用 _lastDailyFire / _firedStartupTasks 打了两个补丁，覆盖不全。
    /// 状态放在服务层持有的仓库里，所有触发器实例共享同一份数据。
    /// </summary>
    public interface ITriggerStateStore
    {
        DateTime? Get(string taskName, string key);
        void Set(string taskName, string key, DateTime value);
        void ForgetRemoved(IEnumerable<string> activeNames);
    }

    internal sealed class TriggerStateFile
    {
        public int Version { get; set; }
        public Dictionary<string, Dictionary<string, long>> Tasks { get; set; }
    }

    /// <summary>纯内存实现：单测与「不需要跨宿主重启」的场景使用。</summary>
    public sealed class InMemoryTriggerStateStore : ITriggerStateStore
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, Dictionary<string, long>> _data =
            new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);

        public DateTime? Get(string taskName, string key)
        {
            lock (_sync)
            {
                Dictionary<string, long> values;
                long ticks;
                if (_data.TryGetValue(Key(taskName), out values) && values.TryGetValue(key ?? "", out ticks))
                    return new DateTime(ticks);
                return null;
            }
        }

        public void Set(string taskName, string key, DateTime value)
        {
            lock (_sync)
            {
                string name = Key(taskName);
                Dictionary<string, long> values;
                if (!_data.TryGetValue(name, out values))
                {
                    values = new Dictionary<string, long>(StringComparer.Ordinal);
                    _data[name] = values;
                }
                values[key ?? ""] = value.Ticks;
            }
        }

        public void ForgetRemoved(IEnumerable<string> activeNames)
        {
            var active = new HashSet<string>(activeNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            lock (_sync)
            {
                var gone = _data.Keys.Where(k => !active.Contains(k)).ToList();
                foreach (var k in gone) _data.Remove(k);
            }
        }

        private static string Key(string taskName)
        {
            return string.IsNullOrWhiteSpace(taskName) ? "?" : taskName.Trim();
        }
    }

    /// <summary>
    /// 落盘实现：`task-trigger-state.json`，沿用 PinGuard / TaskFireLimiter 的范式
    /// （延迟读回、AtomicFile 原子写、损坏或写入失败降级为进程内状态并留痕）。
    /// 用 long ticks 落盘，避免 Newtonsoft 对 DateTime.Kind 的往返歧义。
    /// </summary>
    public sealed class PersistentTriggerStateStore : ITriggerStateStore
    {
        private const int StateVersion = 1;

        public static string DefaultStatePath
        {
            get { return Path.Combine(ConfigService.DirPath, "task-trigger-state.json"); }
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, Dictionary<string, long>> _data =
            new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
        private readonly string _statePath;
        private bool _loaded;

        public PersistentTriggerStateStore(string statePath = null)
        {
            _statePath = string.IsNullOrEmpty(statePath) ? DefaultStatePath : statePath;
        }

        public DateTime? Get(string taskName, string key)
        {
            lock (_sync)
            {
                EnsureLoadedNoLock();
                Dictionary<string, long> values;
                long ticks;
                if (_data.TryGetValue(Key(taskName), out values) && values.TryGetValue(key ?? "", out ticks))
                    return new DateTime(ticks);
                return null;
            }
        }

        public void Set(string taskName, string key, DateTime value)
        {
            lock (_sync)
            {
                EnsureLoadedNoLock();
                string name = Key(taskName);
                Dictionary<string, long> values;
                if (!_data.TryGetValue(name, out values))
                {
                    values = new Dictionary<string, long>(StringComparer.Ordinal);
                    _data[name] = values;
                }
                values[key ?? ""] = value.Ticks;
                PersistNoLock();
            }
        }

        public void ForgetRemoved(IEnumerable<string> activeNames)
        {
            var active = new HashSet<string>(activeNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            lock (_sync)
            {
                EnsureLoadedNoLock();
                var gone = _data.Keys.Where(k => !active.Contains(k)).ToList();
                if (gone.Count == 0) return;
                foreach (var k in gone) _data.Remove(k);
                PersistNoLock();
            }
        }

        private void EnsureLoadedNoLock()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(_statePath)) return;
                var file = JsonConvert.DeserializeObject<TriggerStateFile>(
                    File.ReadAllText(_statePath, Encoding.UTF8));
                if (file == null || file.Version != StateVersion || file.Tasks == null)
                {
                    RecoverNoLock("unsupported version or empty envelope");
                    return;
                }
                foreach (var kv in file.Tasks)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
                    _data[kv.Key] = new Dictionary<string, long>(kv.Value, StringComparer.Ordinal);
                }
            }
            catch (Exception ex)
            {
                RecoverNoLock(ex.Message);
            }
        }

        private void RecoverNoLock(string detail)
        {
            try
            {
                string bak = _statePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(_statePath, bak, true);
                TaskLogger.Warn("system", "trigger state backed up to " + bak + ": " + detail);
            }
            catch { }

            _data.Clear();
            TaskLogger.Warn("system", "trigger state unreadable (" + detail + "), reset to empty");
            PersistNoLock();
        }

        private void PersistNoLock()
        {
            try
            {
                var file = new TriggerStateFile
                {
                    Version = StateVersion,
                    Tasks = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase)
                };
                foreach (var kv in _data)
                    file.Tasks[kv.Key] = new Dictionary<string, long>(kv.Value, StringComparer.Ordinal);

                AtomicFile.WriteAllText(_statePath, JsonConvert.SerializeObject(file, Formatting.Indented), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // 落盘失败绝不改变本次判定：降级为进程内状态
                TaskLogger.Warn("system", "trigger state persist failed, in-memory only: " + ex.Message);
            }
        }

        private static string Key(string taskName)
        {
            return string.IsNullOrWhiteSpace(taskName) ? "?" : taskName.Trim();
        }
    }
}
