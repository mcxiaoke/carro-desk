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
        private IntPtr _hookHandle;

        public event Action<IntPtr, string> ForegroundChanged;

        public string CurrentProcessName { get; private set; }
        public IntPtr CurrentWindowHandle { get; private set; }

        public ForegroundTracker()
        {
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
            catch { }
        }

        private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hWnd == IntPtr.Zero) return;
            string procName = GetProcessName(hWnd);
            CurrentWindowHandle = hWnd;
            CurrentProcessName = procName;
            ForegroundChanged?.Invoke(hWnd, procName);
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
            catch { }
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
