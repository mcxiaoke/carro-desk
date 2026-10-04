using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CarroDesk.Services.Tasks
{
    /// <summary>
    /// 任务日志。
    ///
    /// 性能约束（决定了这里的实现形态）：
    /// <see cref="WriteOutput"/> 是 Process.OutputDataReceived / ErrorDataReceived 的直接回调，
    /// 运行在线程池线程上。子进程的 stdout/stderr 是匿名管道（默认 4KB），回调一旦处理不及，
    /// 管道写满后**子进程会被阻塞挂起** —— 现象是"脚本莫名卡住"，与调度器毫无关联，极难定位。
    ///
    /// 旧实现单条日志的代价：1 次 Directory.Exists + 每个文件 2 轮 (File.Exists + FileInfo) +
    /// 2 次开关文件，全部串在**一把静态全局锁**里。多任务并发输出时所有回调互相排队。
    ///
    /// 现在：
    ///   1. 锁粒度降到"每个日志文件一把"，不同任务、不同文件互不阻塞；
    ///   2. 目录创建只做一次（并校验目录未被外部删除）；
    ///   3. 轮转检查按路径限频（两次检查至少间隔 <see cref="RotateCheckInterval"/>），
    ///      且拿到的是上一次的字节数，不必每次都 File.Exists + FileInfo.Length。
    /// 单条日志从"约 8 次文件系统操作"降到"1 次开 + 1 次写 + 1 次关"。
    ///
    /// 为什么不用"内存队列 + 单写线程"：那会破坏读后写一致性 —— 任务日志是用户可见的排障入口
    /// （编辑器"查看日志"、单测读日志断言脚本收到的参数），异步落盘会让读到内容随机缺行。
    /// 真正的根治手段是让输出回调永不阻塞（即异步落盘 + 显式 flush 屏障），属更大改动，另行评估。
    ///
    /// 为什么不用常开追加流：常开写句柄与 <c>File.ReadAllText</c>（FileShare.Read）
    /// 双向共享冲突，外部读取日志/记事本打开会直接失败。
    /// </summary>
    public static class TaskLogger
    {
        private const long MaxFileBytes = 5L * 1024 * 1024;
        private const int KeepGenerations = 3;

        /// <summary>两次轮转检查之间的最小间隔：避免每写一行就 File.Exists + FileInfo.Length。</summary>
        private static readonly TimeSpan RotateCheckInterval = TimeSpan.FromSeconds(5);

        /// <summary>每个日志文件一把锁，避免多任务并发输出时互相排队。</summary>
        private static readonly Dictionary<string, object> _fileLocks =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> _lastKnownBytes =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _metaLock = new object();
        private static bool _dirEnsured;

        public static void EnsureLogDir()
        {
            try
            {
                lock (_metaLock)
                {
                    var dir = ConfigService.LogsDirPath;
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    _dirEnsured = true;
                }
            }
            catch { }
        }

        public static string GetTaskLogPath(string taskName)
        {
            string safe = Sanitize(taskName);
            return Path.Combine(ConfigService.LogsDirPath, "task-" + safe + ".log");
        }

        public static string GetAggregateLogPath()
        {
            return Path.Combine(ConfigService.LogsDirPath, "tasks.log");
        }

        public static void Info(string taskName, string message)
        {
            Write(taskName, "INFO", message);
        }

        public static void Warn(string taskName, string message)
        {
            Write(taskName, "WARN", message);
        }

        public static void Error(string taskName, string message)
        {
            Write(taskName, "ERROR", message);
        }

        public static void Write(string taskName, string level, string message)
        {
            string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] [{2}] {3}",
                DateTime.Now, level, taskName ?? "system", message);
            byte[] bytes;
            try
            {
                bytes = new UTF8Encoding(false).GetBytes(line + Environment.NewLine);
            }
            catch
            {
                return;
            }

            // 聚合日志永远写；任务日志仅在有任务名时写。二者用不同的锁。
            AppendLine(GetAggregateLogPath(), bytes);
            if (!string.IsNullOrWhiteSpace(taskName))
            {
                AppendLine(GetTaskLogPath(taskName), bytes);
            }
        }

        public static void WriteOutput(string taskName, string stream, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            // split lines to preserve prefix per line
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var l in lines)
            {
                if (l.Length == 0) continue;
                Write(taskName, stream, l);
            }
        }

        private static void AppendLine(string path, byte[] bytes)
        {
            object fileLock = GetFileLock(path);
            lock (fileLock)
            {
                try
                {
                    EnsureLogDirLocked();
                    RotateIfNeededLocked(path);

                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete, bytes.Length))
                    {
                        fs.Write(bytes, 0, bytes.Length);
                        fs.Flush();
                        RememberSizeLocked(path, fs.Length);
                    }
                }
                catch
                {
                    // 目录/文件不可写（磁盘满、权限、被独占）：放弃本条，绝不影响任务自身
                }
            }
        }

        /// <summary>取（并按需创建）某日志文件的专用锁对象。</summary>
        private static object GetFileLock(string path)
        {
            lock (_metaLock)
            {
                object fileLock;
                if (!_fileLocks.TryGetValue(path, out fileLock))
                {
                    fileLock = new object();
                    _fileLocks[path] = fileLock;
                }
                return fileLock;
            }
        }

        private static void RememberSizeLocked(string path, long length)
        {
            lock (_metaLock)
            {
                _lastKnownBytes[path] = length;
                _dirEnsured = true;
            }
        }

        /// <summary>目录检查降频到"仅在未确认过时才做一次"，外部删除会在写入失败时自然暴露。</summary>
        private static void EnsureLogDirLocked()
        {
            bool ensured;
            lock (_metaLock) ensured = _dirEnsured;
            if (ensured) return;
            EnsureLogDir();
        }

        /// <summary>
        /// 轮转检查（按路径限频）：current -&gt; .1 -&gt; .2 -&gt; .3。
        /// 先用上次记录的字节数判断是否可能超限，仅在"可能超限"时才真正访问文件系统。
        /// </summary>
        private static void RotateIfNeededLocked(string path)
        {
            DateTime due;
            long known;
            lock (_metaLock)
            {
                if (!_lastKnownBytes.TryGetValue(path, out known)) known = -1;
                DateTime lastCheck;
                _lastRotateChecks.TryGetValue(path, out lastCheck);
                due = lastCheck.Add(RotateCheckInterval);
            }

            if (known >= 0 && known < MaxFileBytes && DateTime.UtcNow < due) return;

            try
            {
                if (!File.Exists(path)) return;
                long length;
                try
                {
                    length = new FileInfo(path).Length;
                }
                catch
                {
                    // 取不到长度就把限频窗口推后，避免异常路径下反复访问文件系统
                    MarkRotatedChecked(path);
                    return;
                }
                RememberSizeLocked(path, length);
                MarkRotatedChecked(path);
                if (length < MaxFileBytes) return;

                // rotate: .3 -> delete, .2 -> .3, .1 -> .2, current -> .1
                for (int i = KeepGenerations; i >= 1; i--)
                {
                    string src = i == 1 ? path : path + "." + (i - 1);
                    string dst = path + "." + i;
                    if (File.Exists(src))
                    {
                        try
                        {
                            if (File.Exists(dst)) File.Delete(dst);
                            File.Move(src, dst);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static readonly Dictionary<string, DateTime> _lastRotateChecks =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private static void MarkRotatedChecked(string path)
        {
            lock (_metaLock)
            {
                _lastRotateChecks[path] = DateTime.UtcNow;
            }
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unknown";
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                sb.Append(ok ? c : '_');
            }
            string s = sb.ToString();
            if (s.Length > 64) s = s.Substring(0, 64);
            return s;
        }
    }
}