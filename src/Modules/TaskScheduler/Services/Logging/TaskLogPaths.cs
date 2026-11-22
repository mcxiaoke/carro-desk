using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CarroDesk.Services;

namespace CarroDesk.Services.Tasks
{
    /// <summary>
    /// 任务日志路径与保留策略的唯一真源。
    ///
    /// 布局结构：
    ///   scheduler.log            调度器/任务管理事件（不含任何进程输出）
    ///   runs/<task>/<yyyyMM>.log 每个任务每月一个日志文件（当月多次运行顺序追加，无需每月单独目录）
    ///   runs/history.jsonl       运行历史索引（append-only 事件流）
    ///
    /// 目录名追加任务名哈希：Windows 文件系统大小写不敏感，仅做字符白名单清洗时
    /// "Foo" 与 "foo" 会指向同一目录造成串写；哈希同时消除超长名截断后的碰撞。
    /// </summary>
    public static class TaskLogPaths
    {
        /// <summary>运行日志保留天数。</summary>
        public const int RetainDays = 30;

        /// <summary>每个任务保留的运行次数上限。</summary>
        public const int MaxRunsPerTask = 200;

        /// <summary>单次运行日志的输出字节上限（超出后停止落盘，但仍继续读取管道）。</summary>
        public const long MaxRunOutputBytes = 10L * 1024 * 1024;

        /// <summary>runs 目录总字节上限。</summary>
        public const long MaxRunStorageBytes = 200L * 1024 * 1024;

        /// <summary>单行输出最大字符数，超出截断并留标记。</summary>
        public const int MaxLineChars = 64 * 1024;

        /// <summary>管理日志单文件上限与保留代数。</summary>
        public const long SchedulerLogMaxBytes = 5L * 1024 * 1024;
        public const int SchedulerLogGenerations = 3;

        public static string LogsDir { get { return ConfigService.LogsDirPath; } }

        public static string SchedulerLogPath
        {
            get { return Path.Combine(LogsDir, "scheduler.log"); }
        }

        public static string RunsDir
        {
            get { return Path.Combine(LogsDir, "runs"); }
        }

        public static string HistoryIndexPath
        {
            get { return Path.Combine(RunsDir, "history.jsonl"); }
        }

        /// <summary>任务在 runs 下的目录名：清洗后的名字 + 8 位名字哈希。</summary>
        public static string GetTaskDirName(string taskName)
        {
            string name = string.IsNullOrWhiteSpace(taskName) ? "unknown" : taskName.Trim();
            string safe = Sanitize(name);
            string hash;
            try
            {
                byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(name));
                hash = Convert.ToHexString(bytes, 0, 4);
            }
            catch
            {
                hash = "00000000";
            }
            return safe + "-" + hash;
        }

        public static string GetTaskRunsDir(string taskName)
        {
            return Path.Combine(RunsDir, GetTaskDirName(taskName));
        }

        /// <summary>
        /// 获取任务在特定月份的日志路径：runs/<task>/<yyyyMM>.log。
        /// 每任务每月一个文件，当月多次运行顺序追加；runId 参数保留以向后兼容。
        /// </summary>
        public static string GetRunLogPath(string taskName, DateTime startLocal, string runId = null)
        {
            return Path.Combine(GetTaskRunsDir(taskName), startLocal.ToString("yyyyMM") + ".log");
        }

        /// <summary>
        /// 查找指定任务在磁盘上最新的可用日志文件绝对路径。
        /// 优先从 runs/<task>/ 目录扫描所有 *.log（按最后修改时间倒序），
        /// 兜底检查当前月度文件以及旧版 flat 日志 logs/task-<name>.log；不存在时返回 null。
        /// </summary>
        public static string FindLatestLogFile(string taskName, string logsDir = null)
        {
            if (string.IsNullOrWhiteSpace(taskName)) return null;
            string baseLogsDir = string.IsNullOrEmpty(logsDir) ? LogsDir : logsDir;
            string taskRunsDir = Path.Combine(baseLogsDir, "runs", GetTaskDirName(taskName));

            // 1. 扫描任务运行目录下的所有 *.log 文件（包括当前根目录下的 yyyyMM.log，以及可能存在的旧版子目录日志）
            if (Directory.Exists(taskRunsDir))
            {
                try
                {
                    var logFiles = Directory.GetFiles(taskRunsDir, "*.log", SearchOption.AllDirectories);
                    if (logFiles.Length > 0)
                    {
                        var latestFile = logFiles
                            .Select(f => new FileInfo(f))
                            .OrderByDescending(fi => fi.LastWriteTimeUtc)
                            .ThenByDescending(fi => fi.Name, StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault();

                        if (latestFile != null && latestFile.Exists)
                        {
                            return latestFile.FullName;
                        }
                    }
                }
                catch { }
            }

            // 2. 检查当月月度日志文件（哪怕尚未有写入，若已被 touch 存在）
            string currentMonthPath = Path.Combine(taskRunsDir, DateTime.Now.ToString("yyyyMM") + ".log");
            if (File.Exists(currentMonthPath))
            {
                return currentMonthPath;
            }

            // 3. 兼容旧版 logs/task-<name>.log
            string legacyPath = Path.Combine(baseLogsDir, "task-" + taskName.Trim() + ".log");
            if (File.Exists(legacyPath))
            {
                return legacyPath;
            }

            return null;
        }

        /// <summary>转成相对 LogsDir 的、以 '/' 分隔的路径，供 history.jsonl 存储。</summary>
        public static string ToRelative(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return "";
            string root = LogsDir;
            string rel = fullPath;
            if (rel.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring(root.Length);
            return rel.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      .Replace('\\', '/');
        }

        /// <summary>history.jsonl 里的相对路径还原为绝对路径。</summary>
        public static string FromRelative(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return "";
            return Path.Combine(LogsDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        public static void EnsureDirs()
        {
            try
            {
                if (!Directory.Exists(LogsDir)) Directory.CreateDirectory(LogsDir);
                if (!Directory.Exists(RunsDir)) Directory.CreateDirectory(RunsDir);
            }
            catch { }
        }

        /// <summary>字符白名单清洗，仅用于路径可读性；唯一性由名字哈希保证。</summary>
        private static string Sanitize(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                          (c >= '0' && c <= '9') || c == '_' || c == '-';
                sb.Append(ok ? c : '_');
            }
            string s = sb.ToString();
            if (s.Length > 64) s = s.Substring(0, 64);
            return s.Length == 0 ? "unknown" : s;
        }
    }
}
