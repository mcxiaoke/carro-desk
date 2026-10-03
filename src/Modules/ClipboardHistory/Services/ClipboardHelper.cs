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

        // Windows 剪贴板"排除"约定格式（系统自带 Win+V 亦遵守）：
        //   ExcludeClipboardContentFromMonitorProcessing：DWORD 非 0 = 任何监听者都不得处理；
        //   CanIncludeInClipboardHistory：DWORD 0 = 不要纳入剪贴板历史（密码管理器常用）；
        //   CanUploadToCloudClipboard：DWORD 0 = 不要上传到云端剪贴板。
        private const string ExcludeMonitorFormat = "ExcludeClipboardContentFromMonitorProcessing";
        private const string CanIncludeInHistoryFormat = "CanIncludeInClipboardHistory";
        private const string CanUploadToCloudFormat = "CanUploadToCloudClipboard";

        /// <summary>
        /// 安全获取系统剪贴板文本（带 STA 保护与 3 次重试以防并发独占锁）。
        /// 会先检查剪贴板排除格式：命中即返回 false，避免把密码等敏感内容纳入历史。
        /// </summary>
        public static bool TryGetText(out string text, int retryDelayMs = RetryDelayMs)
        {
            text = null;

            for (int i = 0; i < MaxRetries; i++)
            {
                try
                {
                    var data = Clipboard.GetDataObject();
                    if (data == null) return false;
                    if (IsExcludedFromHistory(data)) return false;

                    if (data.GetDataPresent(DataFormats.UnicodeText))
                    {
                        text = data.GetData(DataFormats.UnicodeText) as string;
                    }
                    else if (data.GetDataPresent(DataFormats.Text))
                    {
                        text = data.GetData(DataFormats.Text) as string;
                    }
                    return !string.IsNullOrEmpty(text);
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

        /// <summary>检查剪贴板数据对象是否声明了"不要记录/上传"（DWORD 语义，非 0 为真）。</summary>
        private static bool IsExcludedFromHistory(IDataObject data)
        {
            try
            {
                if (data.GetDataPresent(ExcludeMonitorFormat) && ToDwordTrue(data.GetData(ExcludeMonitorFormat)))
                    return true;
                if (data.GetDataPresent(CanIncludeInHistoryFormat) && !ToDwordTrue(data.GetData(CanIncludeInHistoryFormat)))
                    return true;
                if (data.GetDataPresent(CanUploadToCloudFormat) && !ToDwordTrue(data.GetData(CanUploadToCloudFormat)))
                    return true;
            }
            catch
            {
                // 读取排除格式失败时按"不排除"处理，保持原有行为
            }
            return false;
        }

        private static bool ToDwordTrue(object value)
        {
            if (value == null) return false;
            try
            {
                if (value is int i) return i != 0;
                if (value is uint u) return u != 0;
                if (value is long l) return l != 0;
                if (value is short s) return s != 0;
                if (value is bool b) return b;
                var str = value as string;
                if (str != null)
                {
                    int parsed;
                    return int.TryParse(str, out parsed) && parsed != 0;
                }
                return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0;
            }
            catch
            {
                return false;
            }
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
