using System;
using System.IO;
using System.Text;
using CarroDesk.Services;

namespace CarroDesk.Common
{
    /// <summary>
    /// 无 DI 上下文的兜底日志出口与共享文件轮转辅助：
    /// 所有写入 log.txt 的路径共用 <see cref="LogFileLock"/> 与 <see cref="AppendRotatedLine"/>，
    /// 避免并发写入/轮转冲突。任何 I/O 失败均静默吞掉，不阻断业务。
    /// </summary>
    public static class StaticLog
    {
        public const long DefaultMaxBytes = 1024 * 1024;
        public static readonly object LogFileLock = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void Warn(string category, string message)
        {
            WriteHostLogLine("WARN", category ?? "App", message ?? "", null);
        }

        public static void Critical(string category, string message, Exception ex = null)
        {
            WriteHostLogLine("CRITICAL", category ?? "Host", message ?? "", ex);
        }

        private static void WriteHostLogLine(string level, string category, string message, Exception ex)
        {
            try
            {
                string dir = ConfigService.DirPath;
                if (string.IsNullOrEmpty(dir)) return;
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{category}] {message}";
                if (ex != null) line += Environment.NewLine + ex;
                AppendRotatedLine(Path.Combine(dir, "log.txt"), line, DefaultMaxBytes, LogFileLock);
            }
            catch
            {
                // 日志写入失败不阻断业务
            }
        }

        public static void AppendRotatedLine(string filePath, string line, long maxBytes, object fileLock = null)
        {
            if (string.IsNullOrEmpty(filePath) || line == null) return;
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sync = fileLock ?? LogFileLock;
            lock (sync)
            {
                try
                {
                    if (maxBytes > 0)
                    {
                        var info = new FileInfo(filePath);
                        if (info.Exists && info.Length >= maxBytes)
                        {
                            var backup = filePath + ".1";
                            if (File.Exists(backup)) File.Delete(backup);
                            File.Move(filePath, backup);
                        }
                    }
                }
                catch { /* 轮转失败不阻断本次写入 */ }
                File.AppendAllText(filePath, line + Environment.NewLine, Utf8NoBom);
            }
        }
    }
}
