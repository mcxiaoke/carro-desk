using System.Collections.Generic;

namespace CarroDesk.Modules.AppAutoMute.Models
{
    public class AppAutoMuteConfig
    {
        public bool Enabled { get; set; } = true;
        public string Hotkey { get; set; } = "Ctrl+Win+S";
        public int MuteDelayMs { get; set; } = 1000;
        public int UnmuteDelayMs { get; set; } = 500;
        public List<string> TargetApps { get; set; } = new List<string> { "chrome.exe", "QQMusic.exe", "cloudmusic.exe" };
    }
}
