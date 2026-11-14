using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CarroDesk.Services.Tasks;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 任务日志测试的读取入口。重构后管理日志（scheduler.log）与运行日志
    /// （runs/&lt;task&gt;/&lt;yyyyMM&gt;/&lt;runId&gt;.log）是两套文件，
    /// 测试必须显式选择读哪一套，避免把「分离」这件事掩盖掉。
    /// </summary>
    internal static class TaskLogTestHelper
    {
        public static string ReadSchedulerLog()
        {
            string path = TaskLogger.LogPath;
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
        }

        /// <summary>只取某任务在管理日志里的行（[task=xxx]）。</summary>
        public static string ReadSchedulerLogFor(string taskName)
        {
            string marker = "[task=" + taskName + "]";
            var lines = ReadSchedulerLog().Split('\n')
                .Where(l => l.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);
            return string.Join("\n", lines);
        }

        public static int CountSchedulerLines(string taskName, string contains)
        {
            return ReadSchedulerLogFor(taskName).Split('\n')
                .Count(l => l.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>先把活跃运行的缓冲落盘，再拼接该任务全部 run 日志（按文件名升序）。</summary>
        public static string ReadAllRunLogs(string taskName)
        {
            try { RunHistoryStore.Instance.FlushActive(); } catch { }
            string dir = TaskLogPaths.GetTaskRunsDir(taskName);
            if (!Directory.Exists(dir)) return string.Empty;
            var files = Directory.GetFiles(dir, "*.log", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
            var sb = new StringBuilder();
            foreach (string file in files)
            {
                try { sb.Append(File.ReadAllText(file, Encoding.UTF8)); }
                catch { }
            }
            return sb.ToString();
        }

        public static string ReadLatestRunLog(string taskName)
        {
            try { RunHistoryStore.Instance.FlushActive(); } catch { }
            string[] files = RunLogFiles(taskName);
            if (files.Length == 0) return string.Empty;
            try { return File.ReadAllText(files[files.Length - 1], Encoding.UTF8); }
            catch { return string.Empty; }
        }

        public static string[] RunLogFiles(string taskName)
        {
            string dir = TaskLogPaths.GetTaskRunsDir(taskName);
            if (!Directory.Exists(dir)) return new string[0];
            return Directory.GetFiles(dir, "*.log", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
        }
    }
}
