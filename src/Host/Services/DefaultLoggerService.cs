using System;
using System.Diagnostics;
using System.IO;
using CarroDesk.Common;
using CarroDesk.Core;
using CarroDesk.Services;

namespace CarroDesk.Host.Services
{
    public class DefaultLoggerService : ILoggerService
    {
        /// <summary>日志轮转阈值：单文件超过 1MB 即轮转为 log.txt.1（与 ipc-audit.log 策略一致）。</summary>
        private const long MaxBytes = StaticLog.DefaultMaxBytes;

        public void LogInfo(string module, string message)
        {
            WriteLog("INFO", module, message, null);
        }

        public void LogWarning(string module, string message)
        {
            WriteLog("WARN", module, message, null);
        }

        public void LogError(string module, string message, Exception ex = null)
        {
            WriteLog("ERROR", module, message, ex);
        }

        private void WriteLog(string level, string module, string message, Exception ex)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{module}] {message}";
            if (ex != null)
            {
                line += Environment.NewLine + ex;
            }

            Debug.WriteLine(line);

            try
            {
                string dir = ConfigService.DirPath;
                if (!string.IsNullOrEmpty(dir))
                {
                    StaticLog.AppendRotatedLine(Path.Combine(dir, "log.txt"), line, MaxBytes, StaticLog.LogFileLock);
                }
            }
            catch { }
        }
    }
}
