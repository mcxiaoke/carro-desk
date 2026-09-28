using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CarroDesk
{
    /// <summary>
    /// WinExe 二实例执行 `ctl` 转发时临时挂接父控制台（IPC 设计 §5.3，S3）。
    /// 仅 ctl 转发路径使用——进程随即 Environment.Exit，不做 Detach。
    /// </summary>
    internal static class ParentConsole
    {
        private const int ATTACH_PARENT_PROCESS = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        /// <summary>尽力挂接父控制台并重绑 stdout/stderr；失败静默（输出不可见但不影响退出码）。</summary>
        public static void TryAttach()
        {
            try
            {
                if (!AttachConsole(ATTACH_PARENT_PROCESS)) return;
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError(), Console.OutputEncoding) { AutoFlush = true });
            }
            catch
            {
                // 已有控制台/重定向句柄等场景直接放弃，退出码仍正确
            }
        }
    }
}
