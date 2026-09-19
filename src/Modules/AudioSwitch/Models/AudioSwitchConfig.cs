using System.Collections.Generic;

namespace CarroDesk.Modules.AudioSwitch.Models
{
    public class AudioSwitchConfig
    {
        public bool Enabled { get; set; } = true;
        public string Hotkey { get; set; } = "Ctrl+`";

        // 注意：这两个值是"设备名匹配关键字"，不是界面文案，**不要做本地化**。
        // 它们用于在设备名中做 IndexOf 匹配（如"扬声器 (Realtek)"），
        // 改成英文关键字会让中文系统上识别不出扬声器/耳机，反之亦然。
        // 用户可在设置界面按自己的设备命名自行修改。
        public string SpeakerPattern { get; set; } = "扬声器";
        public string HeadphonePattern { get; set; } = "耳机";
        public bool PlayNotificationSound { get; set; } = true;
        public List<string> ExcludedDevices { get; set; } = new List<string>();
    }
}
