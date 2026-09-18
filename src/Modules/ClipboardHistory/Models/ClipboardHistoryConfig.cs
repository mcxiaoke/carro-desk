using System;

namespace CarroDesk.Modules.ClipboardHistory.Models
{
    public class ClipboardHistoryConfig
    {
        /// <summary>模块总开关</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>是否自动记录剪贴板复制事件</summary>
        public bool AutoRecord { get; set; } = true;

        /// <summary>全局唤出历史窗口快捷键</summary>
        public string Hotkey { get; set; } = "Win+Alt+V";

        /// <summary>预览文本最大字符数（超过该字数自动截断）</summary>
        public int MaxPreviewChars { get; set; } = 100;

        /// <summary>最大保留历史条数</summary>
        public int MaxItems { get; set; } = 1000;

        /// <summary>历史记录最大保留天数（天）</summary>
        public int RetentionDays { get; set; } = 90;

        public ClipboardHistoryConfig Clone()
        {
            return (ClipboardHistoryConfig)MemberwiseClone();
        }

        public static ClipboardHistoryConfig CreateDefault()
        {
            return new ClipboardHistoryConfig();
        }
    }
}
