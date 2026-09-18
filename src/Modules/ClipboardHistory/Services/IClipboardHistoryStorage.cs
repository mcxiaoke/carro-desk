using System.Collections.Generic;
using CarroDesk.Modules.ClipboardHistory.Models;

namespace CarroDesk.Modules.ClipboardHistory.Services
{
    public interface IClipboardHistoryStorage
    {
        List<ClipboardItem> Load();

        void Save(List<ClipboardItem> items);
    }
}
