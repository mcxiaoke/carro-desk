using System.Collections.Generic;
using CarroDesk.Models.Converters;
using Newtonsoft.Json;

namespace CarroDesk.Modules.ScreenLock.Models
{
    public class ScreenLockConfig
    {
        public bool Enabled { get; set; } = true;
        public int IdleMinutes { get; set; } = 5;
        public bool ShowClock { get; set; } = true;
        public double OverlayOpacity { get; set; } = 0.88;

        [JsonConverter(typeof(StringOrStringListConverter))]
        public List<string> ExcludeProcesses { get; set; } = new List<string>();
        public bool UnlockOnResume { get; set; } = true;

        public ScreenLockConfig Clone()
        {
            return new ScreenLockConfig
            {
                Enabled = Enabled,
                IdleMinutes = IdleMinutes,
                ShowClock = ShowClock,
                OverlayOpacity = OverlayOpacity,
                ExcludeProcesses = ExcludeProcesses != null ? new List<string>(ExcludeProcesses) : new List<string>(),
                UnlockOnResume = UnlockOnResume
            };
        }
    }
}
