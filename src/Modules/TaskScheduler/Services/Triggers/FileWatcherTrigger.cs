using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks.Triggers
{
    /// <summary>
    /// 文件/目录监听触发器。
    ///
    /// 并发模型（附录 B P0 修复）：
    ///   - watcher 的创建/销毁与 generation 变更统一在 _lifecycleLock 内；
    ///     Stop() 与 Start() 都递增 generation，Error 自愈任务先捕获 gen，
    ///     重建前必须确认 gen 仍是当前代，否则放弃 —— 杜绝「已停止的旧触发器复活」。
    ///   - 防抖表改为普通 Dictionary + 锁，不再依赖 ConcurrentDictionary.AddOrUpdate
    ///     的 valueFactory 写外部局部变量（该 factory 可能并发多次调用，判定有竞态）。
    /// </summary>
    public class FileWatcherTrigger : ITrigger
    {
        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;

        /// <summary>
        /// 默认缓冲区仅 8KB，短时间大量文件变动即会溢出。
        /// 溢出后 FileSystemWatcher 内部会停止抛事件——若未订阅 Error，
        /// 触发器会**静默失效**且没有任何日志。
        /// </summary>
        private const int WatcherBufferSize = 64 * 1024;

        private readonly object _lifecycleLock = new object();
        private FileSystemWatcher _watcher;
        private int _generation;
        private volatile bool _stopped = true;

        private readonly object _debounceLock = new object();
        private readonly Dictionary<string, DateTime> _fileDebounceMap =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private readonly int _debounceMs;
        private readonly int _readyTimeoutMs;
        private readonly bool _includeSubdirs;

        public FileWatcherTrigger(TaskDefinition task)
        {
            Task = task;
            var tr = task != null ? task.Trigger : null;
            _debounceMs = Clamp(tr != null ? tr.WatchDebounceMs : 500, 0, 60000);
            _readyTimeoutMs = Clamp(tr != null ? tr.WatchReadyTimeoutMs : 3000, 0, 60000);
            _includeSubdirs = tr != null && tr.WatchIncludeSubdirs;
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        /// <summary>仅供电 Error 自愈路径与单测判定「我这一代是否还有效」。</summary>
        internal bool IsGenerationCurrent(int generation)
        {
            return !_stopped && Volatile.Read(ref _generation) == generation;
        }

        internal int CurrentGeneration
        {
            get { return Volatile.Read(ref _generation); }
        }

        public void Start()
        {
            int gen;
            lock (_lifecycleLock)
            {
                DisposeWatcherNoLock();
                gen = ++_generation;
                _stopped = false;
            }
            BuildWatcher(gen);
        }

        private void BuildWatcher(int gen)
        {
            if (!IsGenerationCurrent(gen)) return;

            string path = Task.Trigger.WatchPath ?? "";
            if (string.IsNullOrWhiteSpace(path))
            {
                TaskLogger.Warn(Task.Name, "watch path empty");
                return;
            }
            try { path = Environment.ExpandEnvironmentVariables(path); } catch { }
            string filter = Task.Trigger.WatchFilter ?? "*.*";
            if (string.IsNullOrWhiteSpace(filter)) filter = "*.*";
            string evt = (Task.Trigger.WatchEvent ?? "created").Trim().ToLowerInvariant();

            string dir = path;
            try
            {
                if (File.Exists(path) || (!string.IsNullOrEmpty(Path.GetExtension(path)) && !Directory.Exists(path)))
                {
                    string d = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (!string.IsNullOrEmpty(d) && Directory.Exists(d))
                    {
                        dir = d;
                        filter = Path.GetFileName(path);
                    }
                }
            }
            catch { }

            if (!Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); } catch { }
                if (!Directory.Exists(dir))
                {
                    TaskLogger.Warn(Task.Name, "watch dir not found: " + dir);
                    return;
                }
                // 目录不存在时自动创建是合理需求，但路径写错也会静默建出目录，留下 INFO 便于排查
                TaskLogger.Info(Task.Name, "watch dir auto-created: " + dir);
            }

            try
            {
                // 创建与启用放在同一把生命周期锁内：Stop() 不可能在「已创建但未登记」的窗口里
                // 漏掉这个 watcher；也不会出现旧代自愈任务把新代 watcher 覆盖掉。
                lock (_lifecycleLock)
                {
                    if (_stopped || _generation != gen) return;

                    var watcher = new FileSystemWatcher(dir, filter);
                    watcher.IncludeSubdirectories = _includeSubdirs;
                    watcher.InternalBufferSize = WatcherBufferSize;
                    watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;

                    bool all = evt == "all" || evt == "*";
                    bool known = evt == "created" || evt == "changed" || evt == "deleted" || evt == "renamed" || all;
                    if (evt == "created" || all || !known) watcher.Created += OnEvent;
                    if (evt == "changed" || all)
                    {
                        watcher.Changed += OnEvent;
                        watcher.NotifyFilter |= NotifyFilters.LastWrite;
                    }
                    if (evt == "deleted" || all) watcher.Deleted += OnEvent;
                    if (evt == "renamed" || all) watcher.Renamed += OnRenamed;

                    _watcher = watcher;
                    watcher.Error += OnWatcherError;
                    watcher.EnableRaisingEvents = true;
                }
                TaskLogger.Info(Task.Name, "watch started dir=" + dir + " filter=" + filter +
                    " event=" + evt + " debounceMs=" + _debounceMs + " subdirs=" + _includeSubdirs);
            }
            catch (Exception ex)
            {
                TaskLogger.Warn(Task.Name, "watch start failed: " + ex.Message);
            }
        }

        /// <summary>
        /// 缓冲区溢出等错误：记录并用「同一代」重建 watcher。
        /// 关键：捕获的 gen 若已被 Stop()/Start() 递增，则彻底放弃重建（P0 复活竞态）。
        /// </summary>
        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            string detail = (e != null && e.GetException() != null) ? e.GetException().Message : "unknown";
            TaskLogger.Error(Task.Name, "watch error (likely buffer overflow): " + detail);

            if (_stopped) return;
            int gen = Volatile.Read(ref _generation);

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                if (!IsGenerationCurrent(gen)) return;
                try
                {
                    lock (_lifecycleLock)
                    {
                        if (_stopped || _generation != gen) return;
                        DisposeWatcherNoLock();
                    }
                    BuildWatcher(gen);
                    if (IsGenerationCurrent(gen))
                        TaskLogger.Info(Task.Name, "watch restarted after error");
                }
                catch (Exception ex)
                {
                    TaskLogger.Warn(Task.Name, "watch restart failed: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// true = 本次应被防抖丢弃。读-判-写在 _debounceLock 内一次完成，
        /// 保证同一文件的 N 个并发事件恰好只放行一次。
        /// </summary>
        public bool ShouldDebounce(string fileKey, DateTime now)
        {
            if (string.IsNullOrEmpty(fileKey)) return false;

            lock (_debounceLock)
            {
                if (_fileDebounceMap.Count > 100)
                {
                    List<string> expired = null;
                    foreach (var pair in _fileDebounceMap)
                    {
                        if ((now - pair.Value).TotalSeconds > 10)
                            (expired ?? (expired = new List<string>())).Add(pair.Key);
                    }
                    if (expired != null)
                        foreach (var key in expired) _fileDebounceMap.Remove(key);
                }

                DateTime last;
                if (_debounceMs > 0 && _fileDebounceMap.TryGetValue(fileKey, out last) &&
                    (now - last).TotalMilliseconds < _debounceMs)
                    return true;

                _fileDebounceMap[fileKey] = now;
                return false;
            }
        }

        private static void WaitForFileReady(string path, int maxWaitMs)
        {
            if (string.IsNullOrEmpty(path) || maxWaitMs <= 0) return;
            try
            {
                if (!File.Exists(path)) return;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < maxWaitMs)
                {
                    try
                    {
                        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            return;
                        }
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(150);
                    }
                }
            }
            catch { }
        }

        private void OnEvent(object sender, FileSystemEventArgs e)
        {
            if (_stopped) return;
            int gen = Volatile.Read(ref _generation);
            var now = DateTime.Now;
            string key = e.FullPath ?? e.Name;
            if (ShouldDebounce(key, now)) return;

            var fullPath = e.FullPath ?? "";
            var changeType = e.ChangeType;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                if (!IsGenerationCurrent(gen)) return;   // Stop 之后不得再向服务层派发
                try
                {
                    if (changeType == WatcherChangeTypes.Created || changeType == WatcherChangeTypes.Changed)
                        WaitForFileReady(fullPath, _readyTimeoutMs);
                    if (!IsGenerationCurrent(gen)) return;

                    var h = Fired;
                    if (h != null) h(Task, "watch:" + changeType + "|" + fullPath);
                }
                catch (Exception ex)
                {
                    TaskLogger.Warn(Task.Name, "watch event dispatch failed: " + ex.Message);
                }
            });
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            if (_stopped) return;
            int gen = Volatile.Read(ref _generation);
            var now = DateTime.Now;
            string key = e.FullPath ?? e.Name;
            if (ShouldDebounce(key, now)) return;

            var fullPath = e.FullPath ?? "";
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                if (!IsGenerationCurrent(gen)) return;
                try
                {
                    WaitForFileReady(fullPath, _readyTimeoutMs);
                    if (!IsGenerationCurrent(gen)) return;

                    var h = Fired;
                    if (h != null) h(Task, "watch:renamed|" + fullPath);
                }
                catch (Exception ex)
                {
                    TaskLogger.Warn(Task.Name, "watch rename dispatch failed: " + ex.Message);
                }
            });
        }

        private void DisposeWatcherNoLock()
        {
            try
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Error -= OnWatcherError;
                    _watcher.Created -= OnEvent;
                    _watcher.Changed -= OnEvent;
                    _watcher.Deleted -= OnEvent;
                    _watcher.Renamed -= OnRenamed;
                    _watcher.Dispose();
                }
            }
            catch { }
            _watcher = null;
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                ++_generation;              // 让所有在途的旧代自愈/事件派发失效
                _stopped = true;
                DisposeWatcherNoLock();
                lock (_debounceLock) _fileDebounceMap.Clear();
            }
        }

        public void Dispose() { Stop(); }
    }
}
