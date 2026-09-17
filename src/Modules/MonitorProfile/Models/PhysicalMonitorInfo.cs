using System;

namespace CarroDesk.Modules.MonitorProfile.Models
{
    public class PhysicalMonitorInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int CurrentBrightness { get; set; }
        public int CurrentContrast { get; set; }
        public int MinBrightness { get; set; }
        public int MaxBrightness { get; set; } = 100;
        public int MinContrast { get; set; }
        public int MaxContrast { get; set; } = 100;
        public bool IsWmi { get; set; }
    }
}
