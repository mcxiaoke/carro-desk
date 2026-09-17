namespace CarroDesk.Modules.MonitorProfile.Models
{
    public class MonitorHotkeyConfig
    {
        public string SwitchToDailyMode { get; set; } = "Ctrl+Shift+D";
        public string SwitchToGameMode { get; set; } = "Ctrl+Shift+G";
        public string SwitchToNightMode { get; set; } = "Ctrl+Shift+N";
        public string ManualRefresh { get; set; } = "Ctrl+Shift+R";
        public string IncreaseBrightness { get; set; } = "Ctrl+Shift+Up";
        public string DecreaseBrightness { get; set; } = "Ctrl+Shift+Down";
    }
}
