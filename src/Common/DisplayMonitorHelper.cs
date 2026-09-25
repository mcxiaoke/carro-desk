using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace CarroDesk.Common
{
    public class DisplayMonitorInfo
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int WorkLeft { get; set; }
        public int WorkTop { get; set; }
        public int WorkWidth { get; set; }
        public int WorkHeight { get; set; }
        public bool IsPrimary { get; set; }
    }

    /// <summary>
    /// 纯 Win32 原生显示器枚举器（零依赖 WinForms）
    /// </summary>
    public static class DisplayMonitorHelper
    {
        private const int MONITORINFOF_PRIMARY = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        public static List<DisplayMonitorInfo> GetAllMonitors()
        {
            var list = new List<DisplayMonitorInfo>();

            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
                {
                    var mi = new MONITORINFO();
                    mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));

                    if (GetMonitorInfo(hMonitor, ref mi))
                    {
                        list.Add(new DisplayMonitorInfo
                        {
                            Left = mi.rcMonitor.Left,
                            Top = mi.rcMonitor.Top,
                            Width = mi.rcMonitor.Right - mi.rcMonitor.Left,
                            Height = mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                            WorkLeft = mi.rcWork.Left,
                            WorkTop = mi.rcWork.Top,
                            WorkWidth = mi.rcWork.Right - mi.rcWork.Left,
                            WorkHeight = mi.rcWork.Bottom - mi.rcWork.Top,
                            IsPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0
                        });
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
            }

            // Fallback：若枚举失败，使用主工作区保底
            if (list.Count == 0)
            {
                list.Add(new DisplayMonitorInfo
                {
                    Left = (int)SystemParameters.VirtualScreenLeft,
                    Top = (int)SystemParameters.VirtualScreenTop,
                    Width = (int)SystemParameters.VirtualScreenWidth,
                    Height = (int)SystemParameters.VirtualScreenHeight,
                    WorkLeft = (int)SystemParameters.WorkArea.Left,
                    WorkTop = (int)SystemParameters.WorkArea.Top,
                    WorkWidth = (int)SystemParameters.WorkArea.Width,
                    WorkHeight = (int)SystemParameters.WorkArea.Height,
                    IsPrimary = true
                });
            }

            return list;
        }
    }
}
