using System.Collections.Generic;

namespace CarroDesk.Modules.AudioSwitch.Models
{
    public class AudioSwitchConfig
    {
        public bool Enabled { get; set; } = true;
        public string Hotkey { get; set; } = "Ctrl+`";
        public string SpeakerPattern { get; set; } = "扬声器";
        public string HeadphonePattern { get; set; } = "耳机";
        public bool PlayNotificationSound { get; set; } = true;
        public List<string> ExcludedDevices { get; set; } = new List<string>();
    }
}
