using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CarroDesk
{
    /// <summary>
    /// Win32 原生 API 互操作封装（第二实例窗口定向唤醒与前台激活）。
    /// </summary>
    internal static class NativeMethods
    {
        public const int SW_RESTORE = 9;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        /// <summary>
        /// 唤醒已在运行的实例：将指定数据目录专属的注册消息定向投递给本应用（同进程名）的顶层窗口。
        /// 避开 HWND_BROADCAST 广播风暴和 UIPI 权限隔离陷阱。
        /// </summary>
        /// <param name="messageName">由 ConfigService.ActivateMessageName 生成的专属消息名称</param>
        /// <returns>成功投递消息的目标窗口数</returns>
        public static int NotifyExistingInstance(string messageName)
        {
            if (string.IsNullOrWhiteSpace(messageName)) return 0;

            int msgId = RegisterWindowMessage(messageName);
            if (msgId == 0) return 0;

            string currentProcessName;
            try
            {
                currentProcessName = Process.GetCurrentProcess().ProcessName;
            }
            catch
            {
                currentProcessName = "CarroDesk";
            }

            int currentPid = Environment.ProcessId;
            int postedCount = 0;

            EnumWindows((hwnd, _) =>
            {
                if (!IsSameApplicationWindow(hwnd, currentProcessName, currentPid))
                {
                    return true;
                }

                // 投递专属唤醒消息
                if (PostMessage(hwnd, msgId, IntPtr.Zero, IntPtr.Zero))
                {
                    postedCount++;
                }

                return true;
            }, IntPtr.Zero);

            return postedCount;
        }

        /// <summary>
        /// 判定给定顶层窗口是否属于同名应用的已运行宿主进程（排除当前第二实例自身）。
        /// </summary>
        internal static bool IsSameApplicationWindow(IntPtr hwnd, string currentProcessName, int currentPid)
        {
            GetWindowThreadProcessId(hwnd, out uint windowProcessId);
            if (windowProcessId == 0 || windowProcessId == (uint)currentPid)
            {
                return false;
            }

            try
            {
                using (var proc = Process.GetProcessById((int)windowProcessId))
                {
                    return string.Equals(proc.ProcessName, currentProcessName, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // 目标进程可能在枚举过程中退出或权限受限，静默忽略
                return false;
            }
        }
    }
}
