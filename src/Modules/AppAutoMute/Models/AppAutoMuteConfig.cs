using System.Collections.Generic;

namespace CarroDesk.Modules.AppAutoMute.Models
{
    public class AppAutoMuteConfig
    {
        public bool Enabled { get; set; } = true;
        public string Hotkey { get; set; } = "Ctrl+Win+S";
        public int MuteDelayMs { get; set; } = 1000;
        public int UnmuteDelayMs { get; set; } = 500;
        public string Mode { get; set; } = "Blacklist"; // "Blacklist" or "Whitelist"
        public List<string> TargetApps { get; set; } = new List<string> { "chrome.exe", "QQMusic.exe", "cloudmusic.exe" };

        public AppAutoMuteConfig Clone()
        {
            var copy = new AppAutoMuteConfig();
            CopyTo(copy);
            return copy;
        }

        public void CopyTo(AppAutoMuteConfig target)
        {
            if (target == null) return;
            target.Enabled = Enabled;
            target.Hotkey = Hotkey;
            target.MuteDelayMs = MuteDelayMs;
            target.UnmuteDelayMs = UnmuteDelayMs;
            target.Mode = Mode;
            target.TargetApps = TargetApps != null ? new List<string>(TargetApps) : new List<string>();
        }
    }
}
