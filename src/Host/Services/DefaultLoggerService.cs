using System;
using System.Diagnostics;
using System.IO;
using CarroDesk.Core;
using CarroDesk.Services;

namespace CarroDesk.Host.Services
{
    public class DefaultLoggerService : ILoggerService
    {
        private static readonly object _lock = new object();

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
                    Directory.CreateDirectory(dir);
                    string logFile = Path.Combine(dir, "log.txt");
                    lock (_lock)
                    {
                        File.AppendAllText(logFile, line + Environment.NewLine);
                    }
                }
            }
            catch { }
        }
    }
}
