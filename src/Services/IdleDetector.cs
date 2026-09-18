using System;
using System.Runtime.InteropServices;

namespace CarroDesk.Services
{
    /// <summary>
    /// 提供系统空闲时间底层探测能力。
    /// </summary>
    public static class IdleDetector
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        /// <summary>
        /// 获取自最后一次物理键鼠输入以来的系统空闲毫秒数。
        /// </summary>
        public static uint GetIdleMilliseconds()
        {
            try
            {
                var info = new LASTINPUTINFO();
                info.cbSize = (uint)Marshal.SizeOf(info);
                if (!GetLastInputInfo(ref info)) return 0;
                uint now = unchecked((uint)Environment.TickCount);
                return unchecked(now - info.dwTime);
            }
            catch
            {
                return 0;
            }
        }
    }
}
