using System;

namespace CarroDesk.Modules.ClipboardHistory.Models
{
    public class ClipboardItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string FullText { get; set; } = string.Empty;

        private string _previewText;
        public string PreviewText
        {
            get
            {
                if (string.IsNullOrEmpty(_previewText) && !string.IsNullOrEmpty(FullText))
                {
                    _previewText = BuildPreviewText(FullText);
                }
                return _previewText ?? string.Empty;
            }
            set => _previewText = value;
        }

        public int TextLength { get; set; }

        public DateTime CopiedAt { get; set; } = DateTime.Now;

        public string Hash { get; set; } = string.Empty;

        public bool IsPinned { get; set; }

        public string FormattedTime => CopiedAt.ToString("MM-dd HH:mm:ss");

        public string DisplaySummary
        {
            get
            {
                string singleLine = PreviewText?.Replace("\r\n", " ")?.Replace("\n", " ")?.Replace("\r", " ");
                return $"{(IsPinned ? "📌 " : "")}{singleLine} ({TextLength} 字)";
            }
        }

        public static string BuildPreviewText(string rawText, int maxChars = 300)
        {
            if (string.IsNullOrEmpty(rawText)) return string.Empty;

            // 规范化换行，至多保留前 3 行有效内容
            string normalized = rawText.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            var previewLines = new System.Collections.Generic.List<string>();
            for (int i = 0; i < lines.Length && previewLines.Count < 3; i++)
            {
                string line = lines[i];
                if (previewLines.Count == 0 && string.IsNullOrWhiteSpace(line)) continue;
                previewLines.Add(line.TrimEnd());
            }

            if (previewLines.Count == 0) return string.Empty;

            string joined = string.Join("\n", previewLines);
            if (lines.Length > 3 && !joined.EndsWith("..."))
            {
                joined += "...";
            }

            if (joined.Length > maxChars)
            {
                joined = joined.Substring(0, maxChars) + "...";
            }

            return joined;
        }
    }
}
