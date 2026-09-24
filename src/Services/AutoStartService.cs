using System;
using System.IO;
using Microsoft.Win32;

namespace CarroDesk.Services
{
    public static class AutoStartService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "CarroDesk";

        /// <summary>
        /// 获取当前主可执行文件绝对路径。
        /// 优先使用 Process.GetCurrentProcess().MainModule.FileName，
        /// 避免 Assembly.GetExecutingAssembly() 在类库重构或单文件打包时指向 DLL 或空值。
        /// </summary>
        public static string GetCurrentExecutablePath()
        {
            try
            {
                var path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return path;
            }
            catch { }

            try
            {
                var path = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return path;
            }
            catch { }

            return string.Empty;
        }

        /// <summary>
        /// 检查注册表中是否已正确启用自启且指向当前运行的 exe。
        /// 若注册表存在键值但指向其他历史路径（便携目录移动等），判定为 false（失效态）。
        /// </summary>
        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    if (key == null) return false;
                    var raw = key.GetValue(ValueName) as string;
                    if (string.IsNullOrWhiteSpace(raw)) return false;

                    string currentExe = GetCurrentExecutablePath();
                    if (string.IsNullOrEmpty(currentExe)) return false;

                    string registeredExe = raw.Trim().Trim('\"');
                    return string.Equals(currentExe, registeredExe, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 设置开机自启状态。
        /// </summary>
        /// <param name="enable">是否启用</param>
        /// <returns>操作是否成功（被安全软件或权限拦截时返回 false）</returns>
        public static bool SetEnabled(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null) return false;
                    if (enable)
                    {
                        var exe = GetCurrentExecutablePath();
                        if (string.IsNullOrEmpty(exe)) return false;
                        key.SetValue(ValueName, "\"" + exe + "\"");
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 同步开机自启状态。
        /// 若 enable 为 true，即便已存在键值但路径失效，也会强制更新自愈。
        /// </summary>
        /// <param name="enable">期望状态</param>
        /// <returns>操作是否成功</returns>
        public static bool Sync(bool enable)
        {
            if (enable)
            {
                // 如果当前未启用或注册表路径已失效，则写入/更新为当前路径
                if (!IsEnabled())
                {
                    return SetEnabled(true);
                }
                return true;
            }
            else
            {
                // 如果注册表中存在任意历史键值，均清理
                bool hasEntry = false;
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                    {
                        hasEntry = key != null && key.GetValue(ValueName) != null;
                    }
                }
                catch { }

                if (hasEntry)
                {
                    return SetEnabled(false);
                }
                return true;
            }
        }
    }
}
