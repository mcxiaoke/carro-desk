using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace CarroDesk.Services
{
    /// <summary>
    /// 统一的进程名称处理、输入归一化、容错匹配与运行进程发现辅助类
    /// </summary>
    public static class ProcessHelper
    {
        public class RunningProcessInfo
        {
            public string ProcessName { get; set; }  // 标准带 .exe，小写，如 "chrome.exe"
            public string PureName { get; set; }     // 不带 .exe，如 "chrome"
            public string WindowTitle { get; set; }  // 主窗口标题

            public string DisplayText
            {
                get
                {
                    if (string.IsNullOrWhiteSpace(WindowTitle)) return ProcessName;
                    string title = WindowTitle.Length > 28 ? WindowTitle.Substring(0, 25) + "..." : WindowTitle;
                    return $"{ProcessName} ({title})";
                }
            }

            public override string ToString() => DisplayText;
        }

        /// <summary>
        /// 将用户输入或配置中的进程名称归一化为标准的带 .exe 小写格式（例如 "chrome" -> "chrome.exe", "D:\path\game.exe" -> "game.exe"）
        /// </summary>
        public static string Normalize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            string s = raw.Trim().Trim('"', '\'', '`');
            s = s.Replace("/", "\\");

            try
            {
                s = Path.GetFileName(s);
            }
            catch { }

            if (string.IsNullOrWhiteSpace(s)) return null;

            s = s.Trim();
            if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                s += ".exe";
            }

            return s.ToLowerInvariant();
        }

        /// <summary>
        /// 获取不带 .exe 扩展名的纯进程名（例如 "chrome.exe" -> "chrome"）
        /// </summary>
        public static string NormalizeNameOnly(string raw)
        {
            string norm = Normalize(raw);
            if (string.IsNullOrEmpty(norm)) return null;

            if (norm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return norm.Substring(0, norm.Length - 4);
            }
            return norm;
        }

        /// <summary>
        /// 对列表进行规范化、过滤空项并去重
        /// </summary>
        public static List<string> NormalizeList(IEnumerable<string> rawList)
        {
            var result = new List<string>();
            if (rawList == null) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in rawList)
            {
                string norm = Normalize(item);
                if (!string.IsNullOrEmpty(norm) && seen.Add(norm))
                {
                    result.Add(norm);
                }
            }

            return result;
        }

        /// <summary>
        /// 弹性判断两个进程名称是否匹配（不区分大小写，无论双方是否包含 .exe 或路径）
        /// </summary>
        public static bool IsMatch(string procA, string procB)
        {
            if (string.IsNullOrWhiteSpace(procA) || string.IsNullOrWhiteSpace(procB))
                return false;

            string nameA = NormalizeNameOnly(procA);
            string nameB = NormalizeNameOnly(procB);

            return string.Equals(nameA, nameB, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 判断进程名列表是否包含目标进程（弹性兼容是否带 .exe）
        /// </summary>
        public static bool ContainsProcess(IEnumerable<string> list, string targetProcess)
        {
            if (list == null || string.IsNullOrWhiteSpace(targetProcess))
                return false;

            string targetName = NormalizeNameOnly(targetProcess);
            if (string.IsNullOrEmpty(targetName)) return false;

            foreach (var item in list)
            {
                if (IsMatch(item, targetName))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 获取当前系统中具有主窗口的活动前台应用程序列表（排除自身与 Explorer，按名称排序去重）
        /// </summary>
        public static List<RunningProcessInfo> GetRunningWindowProcesses()
        {
            var result = new List<RunningProcessInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var processes = Process.GetProcesses();
                foreach (var p in processes)
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle))
                        {
                            string pure = p.ProcessName;
                            string norm = Normalize(pure);

                            if (string.IsNullOrEmpty(norm)) continue;
                            if (string.Equals(norm, "carrodesk.exe", StringComparison.OrdinalIgnoreCase)) continue;
                            if (string.Equals(norm, "explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;

                            if (seen.Add(norm))
                            {
                                result.Add(new RunningProcessInfo
                                {
                                    ProcessName = norm,
                                    PureName = pure,
                                    WindowTitle = p.MainWindowTitle
                                });
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            catch { }

            return result.OrderBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
