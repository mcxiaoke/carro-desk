using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CarroDesk.Modules.ClipboardHistory.Models;

namespace CarroDesk.Modules.ClipboardHistory.Services
{
    public class ClipboardHistoryService : IClipboardHistoryService
    {
        private readonly List<ClipboardItem> _items = new List<ClipboardItem>();
        private readonly object _lock = new object();
        private readonly IClipboardHistoryStorage _storage;
        private ClipboardHistoryConfig _config;
        private string _suppressedHash;

        public event Action HistoryChanged;

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _items.Count;
                }
            }
        }

        public ClipboardHistoryService(IClipboardHistoryStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        }

        public void Start(ClipboardHistoryConfig config)
        {
            _config = config ?? new ClipboardHistoryConfig();

            lock (_lock)
            {
                var loaded = _storage.Load();
                _items.Clear();
                if (loaded != null && loaded.Count > 0)
                {
                    _items.AddRange(loaded);
                    ApplyCleanupRulesLocked();
                }
            }

            HistoryChanged?.Invoke();
        }

        public void ApplyConfig(ClipboardHistoryConfig config)
        {
            _config = config ?? new ClipboardHistoryConfig();

            lock (_lock)
            {
                ApplyCleanupRulesLocked();
                _storage.Save(_items);
            }

            HistoryChanged?.Invoke();
        }

        public void RecordText(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText)) return;
            if (_config != null && !_config.AutoRecord) return;

            string hash = ComputeHash(rawText);

            lock (_lock)
            {
                // 1. 防自环检查：若是刚刚双击选中的条目，抑制本次广播
                if (!string.IsNullOrEmpty(_suppressedHash) && _suppressedHash == hash)
                {
                    _suppressedHash = null;
                    return;
                }

                // 2. 去重与 MRU 提升：若已有该条目，则刷新时间戳并移到顶端
                int existingIndex = _items.FindIndex(x => x.Hash == hash);
                if (existingIndex >= 0)
                {
                    var existing = _items[existingIndex];
                    existing.CopiedAt = DateTime.Now;
                    _items.RemoveAt(existingIndex);
                    _items.Insert(0, existing);
                }
                else
                {
                    // 3. 截断预览规则（支持最多显示 3 行与自定义最大字数）
                    int maxChars = _config != null ? Math.Max(10, _config.MaxPreviewChars) : 100;
                    string preview = ClipboardItem.BuildPreviewText(rawText, maxChars);

                    var item = new ClipboardItem
                    {
                        FullText = rawText,
                        PreviewText = preview,
                        TextLength = rawText.Length,
                        CopiedAt = DateTime.Now,
                        Hash = hash
                    };

                    _items.Insert(0, item);
                }

                // 4. 清理规则触发（最大条数与保留天数）
                ApplyCleanupRulesLocked();
                _storage.Save(_items);
            }

            HistoryChanged?.Invoke();
        }

        public void SuppressNext(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_lock)
            {
                _suppressedHash = ComputeHash(text);
            }
        }

        public IReadOnlyList<ClipboardItem> GetItems()
        {
            lock (_lock)
            {
                if (!_items.Any(x => x.IsPinned))
                {
                    return _items.ToList();
                }

                // 置顶项优先排在前，非置顶项随后，各自保持严格录入顺序
                var result = new List<ClipboardItem>(_items.Count);
                result.AddRange(_items.Where(x => x.IsPinned));
                result.AddRange(_items.Where(x => !x.IsPinned));
                return result;
            }
        }

        public void ClearAll()
        {
            ClearAll(preservePinned: true);
        }

        public void ClearAll(bool preservePinned)
        {
            lock (_lock)
            {
                if (preservePinned)
                {
                    _items.RemoveAll(x => !x.IsPinned);
                }
                else
                {
                    _items.Clear();
                }
                _storage.Save(_items);
            }

            HistoryChanged?.Invoke();
        }

        public void RemoveItem(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            lock (_lock)
            {
                int countBefore = _items.Count;
                _items.RemoveAll(x => x.Id == id);
                if (_items.Count != countBefore)
                {
                    _storage.Save(_items);
                }
            }

            HistoryChanged?.Invoke();
        }

        public void TogglePin(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            lock (_lock)
            {
                var item = _items.FirstOrDefault(x => x.Id == id);
                if (item != null)
                {
                    item.IsPinned = !item.IsPinned;
                    _storage.Save(_items);
                }
            }

            HistoryChanged?.Invoke();
        }

        private void ApplyCleanupRulesLocked()
        {
            if (_config == null) return;

            // 规则 1：过期清理（天数，保护已置顶项目）
            if (_config.RetentionDays > 0)
            {
                var cutoff = DateTime.Now.AddDays(-_config.RetentionDays);
                _items.RemoveAll(x => !x.IsPinned && x.CopiedAt < cutoff);
            }

            // 规则 2：最大条数截断（保留最新，从最末尾倒序淘汰未置顶的最老项）
            if (_config.MaxItems > 0 && _items.Count > _config.MaxItems)
            {
                for (int i = _items.Count - 1; i >= 0 && _items.Count > _config.MaxItems; i--)
                {
                    if (!_items[i].IsPinned)
                    {
                        _items.RemoveAt(i);
                    }
                }

                // 极端情况：若置顶项本身就超过 MaxItems，直接截断
                if (_items.Count > _config.MaxItems)
                {
                    _items.RemoveRange(_config.MaxItems, _items.Count - _config.MaxItems);
                }
            }
        }

        private static string ComputeHash(string text)
        {
            using (var md5 = MD5.Create())
            {
                byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(text));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
