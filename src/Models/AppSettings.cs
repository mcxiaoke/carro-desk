using System;

namespace CarroDesk.Models
{
    public class AppSettings
    {
        /// <summary>
        /// 当前配置结构版本。v1 = 历史扁平配置（无版本字段），v2 = 模块分区结构。
        /// 字段缺失时读取端视为 v1，由 ConfigService 的迁移链升级并写回（M6）。
        /// </summary>
        public const int CurrentSchemaVersion = 2;

        /// <summary>
        /// 配置结构版本；新建配置写入 <see cref="CurrentSchemaVersion"/>。
        /// 读取到更高版本时只告警、不降级覆盖未知结构。
        /// </summary>
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>
        /// 开机自启。默认关闭：是否写入 Run 注册表必须由用户在首次运行向导/设置页显式选择（H6）。
        /// </summary>
        public bool AutoStart { get; set; } = false;
        public string PinSalt { get; set; } = "";
        public string PinHash { get; set; } = "";
        public string Language { get; set; } = "auto";
        public IpcSettings Ipc { get; set; } = new IpcSettings();

        // 桌面悬浮控制面板配置
        public string FloatingPanelHotkey { get; set; } = "Win+Alt+C";
        public string FloatingPanelPosition { get; set; } = "Tray"; // "Tray", "Center", "TopRight", "Custom"
        public bool FloatingPanelPinned { get; set; } = false;
        public bool FloatingPanelLocked { get; set; } = false;
        public double FloatingPanelX { get; set; } = -1;
        public double FloatingPanelY { get; set; } = -1;

        public bool HasPin()
        {
            if (string.IsNullOrEmpty(PinHash) || string.IsNullOrEmpty(PinSalt)) return false;
            try
            {
                byte[] salt = Convert.FromBase64String(PinSalt);
                if (salt.Length < 8) return false;

                if (PinHash.StartsWith("pbkdf2$", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = PinHash.Split('$');
                    if (parts.Length != 3) return false;
                    int iterations;
                    if (!int.TryParse(parts[1], out iterations) || iterations <= 0) return false;
                    return Convert.FromBase64String(parts[2]).Length > 0;
                }

                // 兼容旧版单轮 SHA-256(Base64(salt):pin)。
                return Convert.FromBase64String(PinHash).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public AppSettings Clone()
        {
            var c = new AppSettings();
            CopyTo(c);
            return c;
        }

        public void CopyTo(AppSettings target)
        {
            target.SchemaVersion = SchemaVersion;
            target.AutoStart = AutoStart;
            target.PinSalt = PinSalt;
            target.PinHash = PinHash;
            target.Language = Language;
            target.FloatingPanelHotkey = FloatingPanelHotkey;
            target.FloatingPanelPosition = FloatingPanelPosition;
            target.FloatingPanelPinned = FloatingPanelPinned;
            target.FloatingPanelLocked = FloatingPanelLocked;
            target.FloatingPanelX = FloatingPanelX;
            target.FloatingPanelY = FloatingPanelY;
            // 深拷贝：Ipc 节若只传引用，一次 Clone/CopyTo 后两份设置会共享同一可变对象
            target.Ipc = Ipc != null ? Ipc.Clone() : new IpcSettings();
        }

        public static AppSettings Merge(AppSettings loaded)
        {
            var def = new AppSettings();
            if (loaded == null) return def;
            if (string.IsNullOrEmpty(loaded.Language)) loaded.Language = def.Language;
            if (string.IsNullOrEmpty(loaded.FloatingPanelHotkey)) loaded.FloatingPanelHotkey = def.FloatingPanelHotkey;
            if (string.IsNullOrEmpty(loaded.FloatingPanelPosition)) loaded.FloatingPanelPosition = def.FloatingPanelPosition;
            if (loaded.Ipc == null) loaded.Ipc = new IpcSettings();
            return loaded;
        }
    }
}
