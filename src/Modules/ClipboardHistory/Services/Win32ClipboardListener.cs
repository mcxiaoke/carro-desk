using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CarroDesk.Modules.ClipboardHistory.Services
{
    public class Win32ClipboardListener : IClipboardListener
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        private readonly object _lock = new object();
        private HwndSource _hwndSource;
        private bool _isListening;
        private bool _disposed;

        public event Action<string> ClipboardUpdated;

        public bool IsListening => _isListening;

        public void Start()
        {
            lock (_lock)
            {
                if (_disposed || _isListening) return;

                try
                {
                    if (_hwndSource == null)
                    {
                        var parameters = new HwndSourceParameters("CarroDesk_ClipboardListener")
                        {
                            ParentWindow = HWND_MESSAGE
                        };
                        _hwndSource = new HwndSource(parameters);
                        _hwndSource.AddHook(WndProc);
                    }

                    if (_hwndSource.Handle != IntPtr.Zero)
                    {
                        _isListening = AddClipboardFormatListener(_hwndSource.Handle);
                    }
                }
                catch
                {
                    _isListening = false;
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_isListening) return;

                try
                {
                    if (_hwndSource != null && _hwndSource.Handle != IntPtr.Zero)
                    {
                        RemoveClipboardFormatListener(_hwndSource.Handle);
                    }
                }
                catch
                {
                    // 忽略注销异常
                }
                finally
                {
                    _isListening = false;
                }
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE && _isListening)
            {
                try
                {
                    string text;
                    // 在 WM_CLIPBOARDUPDATE 到达时立即捕获文本，避免连续 A→B 更新时
                    // 两个延迟回调都读取到 B 而丢失 A。
                    if (ClipboardHelper.TryGetText(out text, 0) && !string.IsNullOrWhiteSpace(text))
                        ClipboardUpdated?.Invoke(text);
                }
                catch
                {
                    // 异常隔离，防止事件委托崩溃破坏消息泵
                }
            }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                Stop();

                try
                {
                    if (_hwndSource != null)
                    {
                        _hwndSource.RemoveHook(WndProc);
                        _hwndSource.Dispose();
                        _hwndSource = null;
                    }
                }
                catch
                {
                    // 忽略销毁异常
                }
            }
        }
    }
}
