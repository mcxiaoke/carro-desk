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
        public static bool TryGetText(out string text)
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
                    Thread.Sleep(RetryDelayMs);
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
        public static bool TrySetText(string text)
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
                    Thread.Sleep(RetryDelayMs);
                }
                catch (Exception)
                {
                    return false;
                }
            }

            return false;
        }
    }
}
