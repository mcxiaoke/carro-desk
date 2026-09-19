using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CarroDesk.Core;

namespace CarroDesk.Host.Services
{
    public class ForegroundTracker : IForegroundTracker
    {
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

        private const string LogModuleId = "Foreground";

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        // 强引用持有委托，防止被 .NET GC 垃圾回收引发内存非法访问崩溃！
        private readonly WinEventDelegate _hookDelegate;
        private readonly ILoggerService _logger;
        private IntPtr _hookHandle;

        public event Action<IntPtr, string> ForegroundChanged;

        public string CurrentProcessName { get; private set; }
        public IntPtr CurrentWindowHandle { get; private set; }

        public ForegroundTracker(ILoggerService logger = null)
        {
            _logger = logger;
            _hookDelegate = OnWinEvent;
            try
            {
                _hookHandle = SetWinEventHook(
                    EVENT_SYSTEM_FOREGROUND,
                    EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero,
                    _hookDelegate,
                    0,
                    0,
                    WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

                UpdateCurrent();
            }
            catch (Exception ex)
            {
                _logger?.LogError(LogModuleId, "安装前台窗口变更钩子失败，自动静音/保持唤醒的进程联动可能失效", ex);
            }
        }

        private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hWnd == IntPtr.Zero) return;
            string procName = GetProcessName(hWnd);
            CurrentWindowHandle = hWnd;
            CurrentProcessName = procName;

            // SetWinEventHook(WINEVENT_OUTOFCONTEXT) 的回调由 user32 在安装线程的消息泵内直接调用。
            // 订阅者（AppAutoMute / Awake 等）一旦抛异常，异常会沿 native 回调帧逸出，
            // 不保证被 DispatcherUnhandledException 捕获，可能直接终止进程。
            // 因此这里逐订阅者隔离：任何一个失败都不得击穿宿主，也不得影响其它订阅者。
            var handler = ForegroundChanged;
            if (handler == null) return;

            var subscribers = handler.GetInvocationList();
            for (int i = 0; i < subscribers.Length; i++)
            {
                var callback = subscribers[i] as Action<IntPtr, string>;
                if (callback == null) continue;
                SafeInvoker.Run(LogModuleId,
                    () => callback(hWnd, procName),
                    (id, ex) => _logger?.LogError(id, "前台窗口变更订阅者抛出异常", ex));
            }
        }

        public void UpdateCurrent()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                CurrentWindowHandle = hwnd;
                CurrentProcessName = GetProcessName(hwnd);
            }
        }

        private static string GetProcessName(IntPtr hWnd)
        {
            try
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid > 0)
                {
                    using (var proc = Process.GetProcessById((int)pid))
                    {
                        return proc.ProcessName + ".exe";
                    }
                }
            }
            catch
            {
                // 有意静默：窗口在事件投递与本次查询之间被销毁 / 进程已退出是常态，
                // 每个前台切换都可能发生，逐条记日志只会淹没真正有价值的错误。
            }
            return string.Empty;
        }

        public void Dispose()
        {
            if (_hookHandle != IntPtr.Zero)
            {
                try
                {
                    UnhookWinEvent(_hookHandle);
                }
                catch { }
                _hookHandle = IntPtr.Zero;
            }
        }
    }
}
