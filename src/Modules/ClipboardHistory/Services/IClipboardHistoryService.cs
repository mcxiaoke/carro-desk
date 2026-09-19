using System;
using System.Collections.Generic;
using CarroDesk.Modules.ClipboardHistory.Models;

namespace CarroDesk.Modules.ClipboardHistory.Services
{
    public interface IClipboardHistoryService
    {
        event Action HistoryChanged;

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
