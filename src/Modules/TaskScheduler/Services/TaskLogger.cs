using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace CarroDesk.Services.Tasks
{
    /// <summary>
    /// 调度管理日志（scheduler.log）。
    ///
    /// 与运行日志严格分离：这里只记录调度器启停、配置重载、触发/限流/跳过/重试决策、
    /// 状态持久化降级、宿主退出清理等「管理事件」，绝不写子进程 stdout/stderr。
    /// 单次运行日志由 <see cref="RunContext"/> / <see cref="RunLogWriter"/> 写 runs/&lt;task&gt;/&lt;yyyyMM&gt;/&lt;runId&gt;.log，
    /// 运行历史索引由 <see cref="RunHistoryStore"/> 写 runs/history.jsonl。
    ///
    /// 旧实现把每行无条件双写到 tasks.log + task-&lt;name&gt;.log，导致管理事件、任务事件、
    /// 进程输出混在一起；并把 "system" 当成任务名生成了伪任务文件 task-system.log。
    /// 现在 "system" 只是日志行里的一个字段值（[task=system]），不再产生文件。
    ///
    /// 管理日志低频，采用同步追加 + 写前轮转：保证「写后立即可读」，便于排障与单测断言。
    /// 任何写入失败都静默吞掉——日志本身绝不能成为功能故障点。
    /// </summary>
    public static class TaskLogger
    {
        private static readonly object _sync = new object();

        public static string LogPath { get { return TaskLogPaths.SchedulerLogPath; } }

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
            string name = NormalizeTask(taskName);
            string line = string.Format(CultureInfo.InvariantCulture, "{0} [{1}] [task={2}] {3}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                string.IsNullOrEmpty(level) ? "INFO" : level,
                name,
                (message ?? "").Replace("\r", "\\r").Replace("\n", "\\n"));

            byte[] bytes;
            try { bytes = new UTF8Encoding(false).GetBytes(line + Environment.NewLine); }
            catch { return; }

            lock (_sync)
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        string path = TaskLogPaths.SchedulerLogPath;
                        TaskLogPaths.EnsureDirs();
                        RotateIfNeeded(path);
                        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete, bytes.Length))
                        {
                            fs.Write(bytes, 0, bytes.Length);
                            fs.Flush();
                        }
                        return;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        // 日志目录被外部清理：重建后重试一次（沿用 M19 语义）
                        if (attempt > 0) return;
                        TaskLogPaths.EnsureDirs();
                    }
                    catch
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>"system"（或空名）只是字段值，不再对应任何日志文件。</summary>
        private static string NormalizeTask(string taskName)
        {
            if (string.IsNullOrWhiteSpace(taskName)) return "system";
            if (string.Equals(taskName, "system", StringComparison.OrdinalIgnoreCase)) return "system";
            return taskName.Trim();
        }

        private static void RotateIfNeeded(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < TaskLogPaths.SchedulerLogMaxBytes) return;
                for (int i = TaskLogPaths.SchedulerLogGenerations; i >= 1; i--)
                {
                    string src = i == 1 ? path : path + "." + (i - 1);
                    string dst = path + "." + i;
                    if (!File.Exists(src)) continue;
                    try
                    {
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(src, dst);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
