using System;
using Newtonsoft.Json;

namespace CarroDesk.Modules.ClipboardHistory.Models
{
    /// <summary>
    /// 剪贴板敏感信息过滤规则：复制内容只要包含 <see cref="Pattern"/>，就整条丢弃，
    /// 既不入内存历史，也不写入历史文件。
    ///
    /// 设计边界（P1 范围，刻意从简）：
    ///   · 只做字面包含匹配，不做正则——避免回溯超时与难以预期的误杀；
    ///   · 只拦本模块的入库路径，不改写系统剪贴板，也不回溯清理历史中已存在的记录。
    /// </summary>
    public class ClipboardFilterRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>关键字（非空，按字面包含匹配）</summary>
        public string Pattern { get; set; } = string.Empty;

        /// <summary>true = 忽略大小写</summary>
        public bool IgnoreCase { get; set; } = true;

        /// <summary>单条规则启停（在模块总开关之外按条控制）</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>列表展示文本（纯 UI 派生值，不参与配置持久化）</summary>
        [JsonIgnore]
        public string DisplayText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Pattern)) return string.Empty;
                return (Enabled ? string.Empty : "（已停用）")
                       + Pattern
                       + (IgnoreCase ? "（忽略大小写）" : "（区分大小写）");
            }
        }

        public ClipboardFilterRule Clone()
        {
            return new ClipboardFilterRule
            {
                Id = Id,
                Pattern = Pattern,
                IgnoreCase = IgnoreCase,
                Enabled = Enabled
            };
        }
    }
}
