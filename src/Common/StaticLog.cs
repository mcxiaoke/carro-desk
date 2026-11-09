using System;
using System.IO;
using System.Text;
using CarroDesk.Services;

namespace CarroDesk.Common
{
    /// <summary>
    /// 无 DI 上下文的兜底日志出口：静态服务、构造期、释放路径等拿不到
    /// ILoggerService 实例的位置，用它写一行 WARN 到宿主 log.txt。
    ///
    /// 刻意保持极简：只记警告、不做轮转（由 DefaultLoggerService 负责）、
    /// 任何失败都静默吞掉——日志本身绝不能成为功能故障点。
    /// 不弹通知、不写 UI：托盘工具不应因后台失败打扰用户。
    /// </summary>
    public static class StaticLog
    {
        private static readonly object Sync = new object();

        public static void Warn(string category, string message)
        {
            try
            {
                string dir = ConfigService.DirPath;
                if (string.IsNullOrEmpty(dir)) return;
                Directory.CreateDirectory(dir);
                string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] [WARN] [{1}] {2}{3}",
                    DateTime.Now, category ?? "App", message ?? "", Environment.NewLine);
                lock (Sync)
                {
                    File.AppendAllText(Path.Combine(dir, "log.txt"), line, Encoding.UTF8);
                }
            }
            catch
            {
                // 日志写入失败不阻断业务
            }
        }
    }
}
