using System;
using System.Collections.Generic;
using CarroDesk.Modules.ClipboardHistory.Models;

namespace CarroDesk.Modules.ClipboardHistory.Services
{
    public interface IClipboardHistoryService
    {
        event Action HistoryChanged;

        /// <summary>
        /// 敏感信息过滤命中事件（参数：命中的规则、被丢弃文本的字符数）。
        /// 刻意不携带原文——敏感内容不应经日志或通知二次扩散。
        /// </summary>
        event Action<ClipboardFilterRule, int> ContentFiltered;

        int Count { get; }

        void Start(ClipboardHistoryConfig config);

        void ApplyConfig(ClipboardHistoryConfig config);

        void RecordText(string rawText);

        void SuppressNext(string text);

        IReadOnlyList<ClipboardItem> GetItems();

        void ClearAll();

        void ClearAll(bool preservePinned);

        void RemoveItem(string id);

        void TogglePin(string id);
    }
}
