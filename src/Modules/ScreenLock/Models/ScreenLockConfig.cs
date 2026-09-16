using System.Collections.Generic;

namespace CarroDesk.Modules.ScreenLock.Models
{
    public class ScreenLockConfig
    {
        public bool Enabled { get; set; } = true;
        public int IdleMinutes { get; set; } = 5;
        public string PinHash { get; set; } = "";
        public string PinSalt { get; set; } = "";
        public bool ShowClock { get; set; } = true;
        public double OverlayOpacity { get; set; } = 0.88;
        public List<string> ExcludeProcesses { get; set; } = new List<string>();
        public bool UnlockOnResume { get; set; } = true;
    }
}
