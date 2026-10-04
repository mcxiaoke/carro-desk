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
        public string Hotkey { get; set; } = "Ctrl+Alt+L";

        public bool AutoLockEnabled { get; set; } = true;
        public bool DevicePresenceEnabled { get; set; } = false;
        public string TargetDeviceIP { get; set; } = "";
        public int DeviceOfflineGraceSeconds { get; set; } = 30;

        [JsonConverter(typeof(StringOrStringListConverter))]
        public List<string> ExcludeProcesses { get; set; } = new List<string>();
        /// <summary>
        /// Windows 会话解锁后是否自动解除 CarroDesk 伪锁屏。
        ///
        /// 默认为 false：该路径**不校验 PIN**，一旦开启，任何能造成一次 Windows 会话解锁的动作
        /// （含用户自己解锁 Windows）都会免 PIN 解除 CarroDesk 锁屏，使"忘记 PIN"与"绕过保护"
        /// 在行为上不可区分。默认关闭以保证"设了 PIN 就是安全的"这一预期成立。
        /// </summary>
        public bool UnlockOnResume { get; set; } = false;

        public ScreenLockConfig Clone()
        {
            return new ScreenLockConfig
            {
                Enabled = Enabled,
                IdleMinutes = IdleMinutes,
                AutoLockEnabled = AutoLockEnabled,
                DevicePresenceEnabled = DevicePresenceEnabled,
                TargetDeviceIP = TargetDeviceIP,
                DeviceOfflineGraceSeconds = DeviceOfflineGraceSeconds,
                ShowClock = ShowClock,
                OverlayOpacity = OverlayOpacity,
                Hotkey = Hotkey,
                ExcludeProcesses = ExcludeProcesses != null ? new List<string>(ExcludeProcesses) : new List<string>(),
                UnlockOnResume = UnlockOnResume
            };
        }
    }
}
