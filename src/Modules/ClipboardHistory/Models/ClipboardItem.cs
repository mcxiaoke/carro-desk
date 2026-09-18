using System;

namespace CarroDesk.Modules.ClipboardHistory.Models
{
    public class ClipboardItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string FullText { get; set; } = string.Empty;

        public string PreviewText { get; set; } = string.Empty;

        public int TextLength { get; set; }

        public DateTime CopiedAt { get; set; } = DateTime.Now;

        public string Hash { get; set; } = string.Empty;

        public string FormattedTime => CopiedAt.ToString("MM-dd HH:mm:ss");

        public string DisplaySummary => $"{PreviewText} ({TextLength} 字)";
    }
}
