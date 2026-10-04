using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace CarroDesk.Common
{
    /// <summary>
    /// 进程物理内存修剪与工作集优化器。
    /// 在冷启动完成、窗口关闭或进入空闲期时，回收托管堆并修剪未使用的冷页面，
    /// 使常驻托盘状态下的内存占用维持在极低水平。
    /// </summary>
    public static class MemoryOptimizer
    {
        [DllImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSize", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        private static readonly object _syncLock = new object();
        private static DateTime _lastTrimTime = DateTime.MinValue;
        private static CancellationTokenSource _pendingScheduleCts;

        /// <summary>两次自动修剪之间的最小防抖间隔（秒），防止频繁触发引起页面软缺页颠簸。</summary>
        public static int MinIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// 异步排队修剪（带防抖合并）。
        /// 在后台任务中延迟指定毫秒后执行，若期间有新的修剪调度，旧调度会被取消合并。
        /// </summary>
        /// <param name="delayMs">延迟毫秒数，默认 2000ms</param>
        /// <param name="force">是否无视最小间隔强制执行</param>
        public static void ScheduleTrim(int delayMs = 2000, bool force = false)
        {
            lock (_syncLock)
            {
                if (_pendingScheduleCts != null)
                {
                    try { _pendingScheduleCts.Cancel(); } catch { }
                    try { _pendingScheduleCts.Dispose(); } catch { }
                    _pendingScheduleCts = null;
                }

                var cts = new CancellationTokenSource();
                _pendingScheduleCts = cts;

                Task.Delay(Math.Max(100, delayMs), cts.Token).ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    lock (_syncLock)
                    {
                        if (ReferenceEquals(_pendingScheduleCts, cts))
                        {
                            _pendingScheduleCts = null;
                        }
                    }
                    try { cts.Dispose(); } catch { }
                    TrimWorkingSet(force);
                }, TaskScheduler.Default);
            }
        }

        /// <summary>
        /// 仅当物理工作集（WorkingSet）超过指定字节阈值时才执行修剪。
        /// </summary>
        /// <param name="thresholdBytes">物理工作集阈值（默认 40MB）</param>
        /// <param name="force">是否无视最小间隔强制执行</param>
        public static bool TrimIfWorkingSetExceeds(long thresholdBytes = 40 * 1024 * 1024, bool force = false)
        {
            try
            {
                using (var currentProcess = Process.GetCurrentProcess())
                {
                    if (currentProcess.WorkingSet64 >= thresholdBytes)
                    {
                        return TrimWorkingSet(force);
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 立即执行一次托管垃圾回收与物理工作集修剪。
        /// </summary>
        /// <param name="force">是否无视最小间隔强制执行</param>
        /// <returns>是否真正执行了修剪（若在节流期内跳过则返回 false）</returns>
        public static bool TrimWorkingSet(bool force = false)
        {
            lock (_syncLock)
            {
                var now = DateTime.UtcNow;
                if (!force && (now - _lastTrimTime).TotalSeconds < MinIntervalSeconds)
                {
                    return false;
                }
                _lastTrimTime = now;
            }

            try
            {
                // 1. 深度收集托管堆冷对象与终结器
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);

                // 2. 将非活跃冷页还给操作系统（Windows 平台专用）
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    using (var currentProcess = Process.GetCurrentProcess())
                    {
                        SetProcessWorkingSetSize(currentProcess.Handle, (IntPtr)(-1), (IntPtr)(-1));
                    }
                }
                return true;
            }
            catch
            {
                // 内存优化属尽力而为的基础设施，绝不向外击穿异常
                return false;
            }
        }
    }
}
