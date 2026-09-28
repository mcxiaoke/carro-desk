namespace CarroDesk.Models
{
    /// <summary>
    /// IPC 控制通道配置（IPC 设计 §7）。S2 仅含命名管道传输所需字段；
    /// Http / Mcp 子节随对应分期（S4/S5）再扩展，避免出现无人消费的死配置。
    /// </summary>
    public class IpcSettings
    {
        /// <summary>总开关：关闭时不启动任何控制通道传输。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>管道名覆盖。空 = 默认 \\.\pipe\CarroDesk.ctl.&lt;当前用户SID&gt;（防多用户撞名）。</summary>
        public string PipeName { get; set; } = "";

        /// <summary>命令审计开关（ipc-audit.log）。默认开启。</summary>
        public bool AuditEnabled { get; set; } = true;

        public IpcSettings Clone()
        {
            var c = new IpcSettings();
            CopyTo(c);
            return c;
        }

        /// <summary>显式逐字段拷贝——AppSettings 家族不使用反射拷贝，新增字段必须同步此方法（IPC 设计 §7 警示）。</summary>
        public void CopyTo(IpcSettings target)
        {
            target.Enabled = Enabled;
            target.PipeName = PipeName;
            target.AuditEnabled = AuditEnabled;
        }
    }
}
