using System;

namespace CarroDesk.Core
{
    /// <summary>
    /// 前台窗口追踪器：监听 EVENT_SYSTEM_FOREGROUND，暴露前台进程/窗口变化。
    /// </summary>
    public interface IForegroundTracker : IDisposable
    {
        event Action<IntPtr, string> ForegroundChanged;
        string CurrentProcessName { get; }
        IntPtr CurrentWindowHandle { get; }
        void UpdateCurrent();
    }
}