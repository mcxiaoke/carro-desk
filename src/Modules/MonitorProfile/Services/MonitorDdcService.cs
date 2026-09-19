using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CarroDesk.Core;
using CarroDesk.Modules.MonitorProfile.Models;

namespace CarroDesk.Modules.MonitorProfile.Services
{
    /// <summary>
    /// DDC/CI 与 WMI 显示器硬件控制服务。
    /// 优先通过 dxva2.dll (High Level API / Low Level VCP) 控制物理显示器，
    /// 若无物理显示器或设置失败则回退至 WMI (支持笔记本内置屏幕)。
    /// 包含后台异步队列与并发防抖，杜绝阻塞主线程。
    /// </summary>
    public class MonitorDdcService
    {
        private readonly ILoggerService _logger;
        private const string LogTag = "MonitorDdc";

        #region Native Interop

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(
            IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
            IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(
            IntPtr hMonitor, uint dwPhysicalMonitorArraySize,
            [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorBrightness(
            IntPtr hMonitor, out uint pdwMinimum, out uint pdwCurrent, out uint pdwMaximum);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetMonitorBrightness(
            IntPtr hMonitor, uint dwNewBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorContrast(
            IntPtr hMonitor, out uint pdwMinimum, out uint pdwCurrent, out uint pdwMaximum);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetMonitorContrast(
            IntPtr hMonitor, uint dwNewContrast);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetVCPFeatureAndVCPFeatureReply(
            IntPtr hMonitor, byte bVCPCode, out uint pvct,
            out uint pdwCurrentValue, out uint pdwMaximumValue);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetVCPFeature(
            IntPtr hMonitor, byte bVCPCode, uint dwNewValue);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        private const byte VCP_BRIGHTNESS = 0x10;
        private const byte VCP_CONTRAST = 0x12;

        #endregion

        #region Async Queue & Debounce

        private readonly object _syncLock = new object();
        private int _pendingBrightness = -1;
        private int _pendingContrast = -1;
        private bool _isApplying = false;

        /// <summary>
        /// 物理显示器（DDC/CI over I2C）访问串行化锁。
        ///
        /// 原先只有防抖队列的 Async 入口受 _syncLock 保护，而
        /// <see cref="SetBrightnessAndContrastSync"/> 会被 ProfileScheduleEngine 的
        /// 后台线程直接调用，设置界面与 30s 轮询也会并发枚举物理显示器——
        /// 多个线程在不同句柄上并发读写同一面板会相互干扰、加剧 I2C 超时并可能读到错误值。
        ///
        /// 约定：本锁与 _syncLock 不会同时持有（_syncLock 只在防抖字段读写时短暂持有，
        /// 调用 Sync 前已释放），因此不存在锁序反转。
        /// </summary>
        private readonly object _ddcLock = new object();

        #endregion

        public MonitorDdcService(ILoggerService logger = null)
        {
            _logger = logger;
        }

        private void Log(string message)
        {
            _logger?.LogInfo(LogTag, message);
        }

        private void LogError(string message, Exception ex = null)
        {
            _logger?.LogError(LogTag, message, ex);
        }

        /// <summary>
        /// 异步队列设置显示器亮度和对比度，自动并发合并防抖。
        /// </summary>
        public Task<int> SetBrightnessAndContrastAsync(int brightness, int contrast)
        {
            brightness = Math.Max(0, Math.Min(100, brightness));
            contrast = Math.Max(0, Math.Min(100, contrast));

            lock (_syncLock)
            {
                _pendingBrightness = brightness;
                _pendingContrast = contrast;

                if (_isApplying)
                {
                    return Task.FromResult(0);
                }

                _isApplying = true;
            }

            return Task.Run(() =>
            {
                int totalApplied = 0;
                while (true)
                {
                    int b, c;
                    lock (_syncLock)
                    {
                        b = _pendingBrightness;
                        c = _pendingContrast;
                        _pendingBrightness = -1;
                        _pendingContrast = -1;
                    }

                    if (b >= 0 && c >= 0)
                    {
                        totalApplied = SetBrightnessAndContrastSync(b, c);
                    }

                    lock (_syncLock)
                    {
                        if (_pendingBrightness < 0 && _pendingContrast < 0)
                        {
                            _isApplying = false;
                            break;
                        }
                    }
                }
                return totalApplied;
            });
        }

        /// <summary>
        /// 同步设置所有显示器的亮度和对比度。
        /// 通过 <see cref="_ddcLock"/> 与其它显示器访问路径互斥。
        /// </summary>
        public int SetBrightnessAndContrastSync(int brightness, int contrast)
        {
            lock (_ddcLock)
            {
                return SetBrightnessAndContrastCore(brightness, contrast);
            }
        }

        private int SetBrightnessAndContrastCore(int brightness, int contrast)
        {
            brightness = Math.Max(0, Math.Min(100, brightness));
            contrast = Math.Max(0, Math.Min(100, contrast));

            var physicalHandles = GetPhysicalMonitorHandles();

            if (physicalHandles.Count == 0)
            {
                Log("DDC: 未检测到物理显示器，回退至 WMI 设置亮度");
                return SetBrightnessViaWmi(brightness) ? 1 : 0;
            }

            int successCount = 0;
            try
            {
                foreach (var handle in physicalHandles)
                {
                    bool bOk = SetBrightnessValue(handle, brightness);
                    bool cOk = SetContrastValue(handle, contrast);

                    if (bOk || cOk)
                    {
                        successCount++;
                    }
                    else
                    {
                        int err = Marshal.GetLastWin32Error();
                        Log($"DDC: 显示器句柄 {handle} 设置失败 (Win32错误: {err})");
                    }
                }
            }
            finally
            {
                foreach (var handle in physicalHandles)
                {
                    DestroyPhysicalMonitor(handle);
                }
            }

            if (successCount == 0)
            {
                Log("DDC: 所有物理显示器设置失败，回退至 WMI 设置亮度");
                return SetBrightnessViaWmi(brightness) ? 1 : 0;
            }

            return successCount;
        }

        /// <summary>
        /// 获取主显示器当前亮度和对比度。
        /// 通过 <see cref="_ddcLock"/> 与其它显示器访问路径互斥。
        /// </summary>
        public bool GetBrightnessAndContrast(out int brightness, out int contrast)
        {
            lock (_ddcLock)
            {
                return GetBrightnessAndContrastCore(out brightness, out contrast);
            }
        }

        private bool GetBrightnessAndContrastCore(out int brightness, out int contrast)
        {
            brightness = 0;
            contrast = 0;

            var physicalHandles = GetPhysicalMonitorHandles();
            try
            {
                if (physicalHandles.Count == 0)
                {
                    if (GetBrightnessViaWmi(out brightness))
                    {
                        contrast = 50;
                        return true;
                    }
                    return false;
                }

                bool bOk = GetBrightnessValue(physicalHandles[0], out brightness);
                bool cOk = GetContrastValue(physicalHandles[0], out contrast);

                if (!bOk && !cOk)
                {
                    if (GetBrightnessViaWmi(out brightness))
                    {
                        contrast = 50;
                        return true;
                    }
                    return false;
                }

                return bOk || cOk;
            }
            finally
            {
                foreach (var handle in physicalHandles)
                {
                    DestroyPhysicalMonitor(handle);
                }
            }
        }

        /// <summary>
        /// 获取当前系统检测到的所有显示器信息列表。
        /// 通过 <see cref="_ddcLock"/> 与其它显示器访问路径互斥。
        /// </summary>
        public List<PhysicalMonitorInfo> GetMonitorsInfo()
        {
            lock (_ddcLock)
            {
                return GetMonitorsInfoCore();
            }
        }

        private List<PhysicalMonitorInfo> GetMonitorsInfoCore()
        {
            var list = new List<PhysicalMonitorInfo>();
            var physicalMonitors = GetPhysicalMonitorInfos();

            try
            {
                int index = 1;
                foreach (var item in physicalMonitors)
                {
                    var info = new PhysicalMonitorInfo
                    {
                        Id = $"MONITOR_{index}",
                        Description = string.IsNullOrEmpty(item.Description) ? $"Display #{index}" : item.Description,
                        IsWmi = false
                    };

                    uint minB, curB, maxB;
                    if (GetMonitorBrightness(item.Handle, out minB, out curB, out maxB))
                    {
                        info.MinBrightness = (int)minB;
                        info.MaxBrightness = (int)maxB;
                        info.CurrentBrightness = maxB > minB
                            ? (int)Math.Round((curB - minB) * 100.0 / (maxB - minB))
                            : (int)curB;
                    }
                    else
                    {
                        int v;
                        if (GetVcpValue(item.Handle, VCP_BRIGHTNESS, out v))
                        {
                            info.CurrentBrightness = v;
                        }
                    }

                    uint minC, curC, maxC;
                    if (GetMonitorContrast(item.Handle, out minC, out curC, out maxC))
                    {
                        info.MinContrast = (int)minC;
                        info.MaxContrast = (int)maxC;
                        info.CurrentContrast = maxC > minC
                            ? (int)Math.Round((curC - minC) * 100.0 / (maxC - minC))
                            : (int)curC;
                    }
                    else
                    {
                        int v;
                        if (GetVcpValue(item.Handle, VCP_CONTRAST, out v))
                        {
                            info.CurrentContrast = v;
                        }
                    }

                    list.Add(info);
                    index++;
                }
            }
            finally
            {
                foreach (var item in physicalMonitors)
                {
                    DestroyPhysicalMonitor(item.Handle);
                }
            }

            if (list.Count == 0)
            {
                int wmiB;
                if (GetBrightnessViaWmi(out wmiB))
                {
                    list.Add(new PhysicalMonitorInfo
                    {
                        Id = "WMI_INTERNAL",
                        Description = "Internal Display (WMI)",
                        CurrentBrightness = wmiB,
                        CurrentContrast = 50,
                        IsWmi = true
                    });
                }
            }

            return list;
        }

        /// <summary>
        /// 检测显示器数量。
        /// 通过 <see cref="_ddcLock"/> 与其它显示器访问路径互斥。
        /// </summary>
        public int DetectMonitorCount()
        {
            lock (_ddcLock)
            {
                return DetectMonitorCountCore();
            }
        }

        private int DetectMonitorCountCore()
        {
            var handles = GetPhysicalMonitorHandles();
            try
            {
                if (handles.Count > 0) return handles.Count;
            }
            finally
            {
                foreach (var h in handles)
                {
                    DestroyPhysicalMonitor(h);
                }
            }

            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightness"))
                using (var results = searcher.Get())
                {
                    return results.Count;
                }
            }
            catch (Exception ex)
            {
                LogError("WMI 检测显示器数量失败", ex);
                return 0;
            }
        }

        #region Physical Handle Enumeration

        private struct HandleWithDesc
        {
            public IntPtr Handle;
            public string Description;
        }

        private List<IntPtr> GetPhysicalMonitorHandles()
        {
            var handles = new List<IntPtr>();
            var infos = GetPhysicalMonitorInfos();
            foreach (var info in infos)
            {
                handles.Add(info.Handle);
            }
            return handles;
        }

        private List<HandleWithDesc> GetPhysicalMonitorInfos()
        {
            var results = new List<HandleWithDesc>();

            MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
            {
                uint count;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out count))
                {
                    return true;
                }

                if (count == 0) return true;

                var physicalMonitors = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicalMonitors))
                {
                    return true;
                }

                foreach (var pm in physicalMonitors)
                {
                    results.Add(new HandleWithDesc
                    {
                        Handle = pm.hPhysicalMonitor,
                        Description = pm.szPhysicalMonitorDescription
                    });
                }

                return true;
            };

            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                Log($"DDC: EnumDisplayMonitors 失败 (Win32错误: {err})");
            }

            GC.KeepAlive(callback);
            return results;
        }

        #endregion

        #region High Level API & VCP Fallback

        private bool SetBrightnessValue(IntPtr handle, int value)
        {
            uint min, current, max;
            if (GetMonitorBrightness(handle, out min, out current, out max))
            {
                uint scaledValue = max > min
                    ? (uint)Math.Round(min + (value / 100.0) * (max - min))
                    : (uint)value;

                if (SetMonitorBrightness(handle, scaledValue))
                {
                    return true;
                }
            }

            return SetVcpValue(handle, VCP_BRIGHTNESS, value);
        }

        private bool GetBrightnessValue(IntPtr handle, out int value)
        {
            value = 0;
            uint min, current, max;
            if (GetMonitorBrightness(handle, out min, out current, out max))
            {
                value = max > min
                    ? (int)Math.Round((current - min) * 100.0 / (max - min))
                    : (int)current;
                return true;
            }

            return GetVcpValue(handle, VCP_BRIGHTNESS, out value);
        }

        private bool SetContrastValue(IntPtr handle, int value)
        {
            uint min, current, max;
            if (GetMonitorContrast(handle, out min, out current, out max))
            {
                uint scaledValue = max > min
                    ? (uint)Math.Round(min + (value / 100.0) * (max - min))
                    : (uint)value;

                if (SetMonitorContrast(handle, scaledValue))
                {
                    return true;
                }
            }

            return SetVcpValue(handle, VCP_CONTRAST, value);
        }

        private bool GetContrastValue(IntPtr handle, out int value)
        {
            value = 0;
            uint min, current, max;
            if (GetMonitorContrast(handle, out min, out current, out max))
            {
                value = max > min
                    ? (int)Math.Round((current - min) * 100.0 / (max - min))
                    : (int)current;
                return true;
            }

            return GetVcpValue(handle, VCP_CONTRAST, out value);
        }

        private bool SetVcpValue(IntPtr handle, byte vcpCode, int value)
        {
            uint unused, currentValue, maxValue;
            if (!GetVCPFeatureAndVCPFeatureReply(handle, vcpCode, out unused, out currentValue, out maxValue))
            {
                return false;
            }

            uint scaledValue = maxValue > 0
                ? (uint)Math.Round(value / 100.0 * maxValue)
                : (uint)value;

            return SetVCPFeature(handle, vcpCode, scaledValue);
        }

        private bool GetVcpValue(IntPtr handle, byte vcpCode, out int value)
        {
            value = 0;
            uint unused, currentValue, maxValue;
            if (!GetVCPFeatureAndVCPFeatureReply(handle, vcpCode, out unused, out currentValue, out maxValue))
            {
                return false;
            }

            value = maxValue > 0
                ? (int)Math.Round(currentValue * 100.0 / maxValue)
                : (int)currentValue;

            return true;
        }

        #endregion

        #region WMI Fallback

        private bool SetBrightnessViaWmi(int brightness)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            obj.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)brightness });
                            Log($"WMI: 亮度已成功设置为 {brightness}");
                            return true;
                        }
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                LogError($"WMI 设置亮度失败 ({brightness})", ex);
                return false;
            }
        }

        private bool GetBrightnessViaWmi(out int brightness)
        {
            brightness = 0;
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            brightness = Convert.ToInt32(obj["CurrentBrightness"]);
                            return true;
                        }
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                LogError("WMI 获取当前亮度失败", ex);
                return false;
            }
        }

        #endregion
    }
}
