using System;
using System.Collections.Generic;

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

        /// <summary>
        /// 单条记录的最大存储字符数（默认 100 万）：超长文本先截断再入库，
        /// 避免几十 MB 的日志/JSON 常驻内存并反复全量落盘。0 表示不限制。
        /// </summary>
        public int MaxTextLength { get; set; } = 1000000;

        /// <summary>最大保留历史条数</summary>
        public int MaxItems { get; set; } = 1000;

        /// <summary>历史记录最大保留天数（天）</summary>
        public int RetentionDays { get; set; } = 90;

        /// <summary>
        /// 是否以 DPAPI 密文格式落盘（绑定本机当前 Windows 用户）。
        /// 只决定写入格式；读取靠文件头自识别，不依赖此配置。
        /// </summary>
        public bool EncryptStorage { get; set; }

        /// <summary>
        /// 敏感信息过滤总开关。默认关闭：新增功能不得改变既有用户的记录行为，
        /// 必须由用户在托盘里显式开启。
        /// </summary>
        public bool FilterEnabled { get; set; }

        /// <summary>
        /// 敏感信息过滤规则（命中即整条丢弃）。规则本身以明文保存在 config.json：
        /// 它只是匹配用的关键字，不含被拦截的内容。
        /// </summary>
        public List<ClipboardFilterRule> FilterRules { get; set; } = new List<ClipboardFilterRule>();

        /// <summary>过滤规则条数上限（设置界面校验，防止误配置拖慢每次复制的匹配开销）</summary>
        public const int MaxFilterRules = 200;

        /// <summary>单个关键字长度上限</summary>
        public const int MaxFilterPatternLength = 256;

        /// <summary>
        /// 深拷贝。
        ///
        /// 基类 MemberwiseClone 是浅拷贝，而本配置含 List&lt;ClipboardFilterRule&gt;：
        /// 设置窗口依赖 Clone() 做"保存失败回滚"，若列表共享引用，回滚会连同新规则一起失效。
        /// </summary>
        public ClipboardHistoryConfig Clone()
        {
            var copy = (ClipboardHistoryConfig)MemberwiseClone();
            copy.FilterRules = new List<ClipboardFilterRule>();
            if (FilterRules != null)
            {
                foreach (var rule in FilterRules)
                {
                    if (rule != null) copy.FilterRules.Add(rule.Clone());
                }
            }
            return copy;
        }

        public static ClipboardHistoryConfig CreateDefault()
        {
            return new ClipboardHistoryConfig();
        }
    }
}
