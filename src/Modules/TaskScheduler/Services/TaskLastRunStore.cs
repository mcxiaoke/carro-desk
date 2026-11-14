using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CarroDesk.Common;
using Newtonsoft.Json;

namespace CarroDesk.Services.Tasks
{
    internal sealed class TaskLastRunFile
    {
        public int Version { get; set; }
        public Dictionary<string, long> Tasks { get; set; }
    }

    /// <summary>
    /// 每个任务「上一次真正启动」的时刻（本地时间），持久化到 task-last-run.json。
    ///
    /// 为什么不用 TaskStateSnapshot.LastFinishedAt：那是进程内状态机的内存快照，
    /// 宿主重启 / 配置重载后即为空，管理窗口的「最近运行」列会整列变 "--"。
    /// 记录点是进程 spawn 成功（wait 的 onStarted 回调 / detach 启动成功），
    /// 因此限流拒绝、条件不满足、启动失败都不会误记。
    ///
    /// 落盘范式与 PinGuard / TaskFireLimiter / TriggerStateStore 一致：
    /// 延迟读回、AtomicFile 原子写、失败或文件损坏降级为进程内状态并留痕。
    /// </summary>
    public sealed class TaskLastRunStore
    {
        private const int StateVersion = 1;

        public static string DefaultStatePath
        {
            get { return Path.Combine(ConfigService.DirPath, "task-last-run.json"); }
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, long> _data =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly string _statePath;
        private bool _loaded;

        public TaskLastRunStore(string statePath = null)
        {
            _statePath = string.IsNullOrEmpty(statePath) ? DefaultStatePath : statePath;
        }

        public DateTime? Get(string taskName)
        {
            lock (_sync)
            {
                EnsureLoadedNoLock();
                long ticks;
                if (_data.TryGetValue(Key(taskName), out ticks)) return new DateTime(ticks);
                return null;
            }
        }

        public void Record(string taskName, DateTime whenLocal)
        {
            lock (_sync)
            {
                EnsureLoadedNoLock();
                _data[Key(taskName)] = whenLocal.Ticks;
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
                var file = JsonConvert.DeserializeObject<TaskLastRunFile>(
                    File.ReadAllText(_statePath, Encoding.UTF8));
                if (file == null || file.Version != StateVersion || file.Tasks == null)
                {
                    RecoverNoLock("unsupported version or empty envelope");
                    return;
                }
                foreach (var kv in file.Tasks)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value <= 0) continue;
                    _data[kv.Key] = kv.Value;
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
                TaskLogger.Warn("system", "last-run state backed up to " + bak + ": " + detail);
            }
            catch { }

            _data.Clear();
            TaskLogger.Warn("system", "last-run state unreadable (" + detail + "), reset to empty");
            PersistNoLock();
        }

        private void PersistNoLock()
        {
            try
            {
                var file = new TaskLastRunFile
                {
                    Version = StateVersion,
                    Tasks = new Dictionary<string, long>(_data, StringComparer.OrdinalIgnoreCase)
                };
                AtomicFile.WriteAllText(_statePath, JsonConvert.SerializeObject(file, Formatting.Indented), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                TaskLogger.Warn("system", "last-run state persist failed, in-memory only: " + ex.Message);
            }
        }

        private static string Key(string taskName)
        {
            return string.IsNullOrWhiteSpace(taskName) ? "?" : taskName.Trim();
        }
    }
}
