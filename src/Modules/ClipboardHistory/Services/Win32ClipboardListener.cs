using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using CarroDesk.Common;

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
        private readonly Dispatcher _dispatcher;
        private HwndSource _hwndSource;
        private bool _isListening;
        private bool _disposed;

        public Win32ClipboardListener(Dispatcher dispatcher = null)
        {
            _dispatcher = dispatcher;
        }

        public event Action<string> ClipboardUpdated;

        public bool IsListening => _isListening;

        private void RunOnDispatcher(Action action)
        {
            if (_dispatcher == null || _dispatcher.CheckAccess())
            {
                action();
                return;
            }
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
            try { _dispatcher.Invoke(action); } catch { }
        }

        public void Start()
        {
            RunOnDispatcher(StartCore);
        }

        private void StartCore()
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
                        if (!_isListening)
                        {
                            StaticLog.Warn("Clipboard", "AddClipboardFormatListener 失败，剪贴板历史不会自动记录 (err=" +
                                           Marshal.GetLastWin32Error() + ")");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _isListening = false;
                    StaticLog.Warn("Clipboard", "剪贴板监听启动失败，历史不会自动记录: " + ex.Message);
                }
            }
        }

        public void Stop()
        {
            if (_dispatcher == null || _dispatcher.CheckAccess() || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                StopCore();
            }
            else
            {
                try { _dispatcher.Invoke(StopCore); } catch { StopCore(); }
            }
        }

        private void StopCore()
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
            if (_dispatcher == null || _dispatcher.CheckAccess() || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                DisposeCore();
            }
            else
            {
                try { _dispatcher.Invoke(DisposeCore); } catch { DisposeCore(); }
            }
        }

        private void DisposeCore()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                StopCore();

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
