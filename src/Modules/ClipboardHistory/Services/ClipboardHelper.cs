using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace CarroDesk.Modules.ClipboardHistory.Services
{
    public static class ClipboardHelper
    {
        private const int MaxRetries = 3;
        private const int RetryDelayMs = 50;

        /// <summary>
        /// 安全获取系统剪贴板文本（带 STA 保护与 3 次重试以防并发独占锁）
        /// </summary>
        public static bool TryGetText(out string text, int retryDelayMs = RetryDelayMs)
        {
            text = null;

            for (int i = 0; i < MaxRetries; i++)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        text = Clipboard.GetText();
                        return true;
                    }
                    return false;
                }
                catch (ExternalException)
                {
                    // 剪贴板被其他应用锁定，等待重试
                    if (retryDelayMs > 0) Thread.Sleep(retryDelayMs);
                }
                catch (Exception)
                {
                    // 其他意外异常直接返回 false
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// 安全设置系统剪贴板纯文本（带 STA 保护与重试）
        /// </summary>
        public static bool TrySetText(string text, int retryDelayMs = RetryDelayMs)
        {
            if (text == null) return false;

            for (int i = 0; i < MaxRetries; i++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    return true;
                }
                catch (ExternalException)
                {
                    if (retryDelayMs > 0) Thread.Sleep(retryDelayMs);
                }
                catch (Exception)
                {
                    return false;
                }
            }

            return false;
        }

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private const byte VK_CONTROL = 0x11;
        private const byte VK_V = 0x56;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        /// <summary>
        /// 异步模拟发送 Ctrl+V 快捷键将剪贴板内容粘贴至前台目标窗口
        /// </summary>
        public static void SimulatePaste(IntPtr expectedTargetWindow, int delayMs = 60)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (delayMs > 0) Thread.Sleep(delayMs);
                    // 若前台已被其它窗口抢走，放弃粘贴，绝不把内容发送到错误目标。
                    if (expectedTargetWindow == IntPtr.Zero || GetForegroundWindow() != expectedTargetWindow)
                        return;
                    keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_V, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                }
                catch { }
            });
        }
    }
}
