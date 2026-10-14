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
        private DateTime _suppressedUntil;

        /// <summary>
        /// 敏感信息过滤规则集，随配置重建（Start / ApplyConfig）。
        /// RecordText 在锁外读取，故声明为 volatile 保证配置重载线程的写入可见。
        /// </summary>
        private volatile ClipboardTextFilter _filter = ClipboardTextFilter.Empty;

        public event Action HistoryChanged;

        public event Action<ClipboardFilterRule, int> ContentFiltered;

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
                _filter = ClipboardTextFilter.FromConfig(_config);

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
                // 规则集重建放在锁内，与配置应用保持同一次原子切换
                _filter = ClipboardTextFilter.FromConfig(_config);

                int maxChars = _config != null ? Math.Max(10, _config.MaxPreviewChars) : 100;
                foreach (var item in _items)
                {
                    if (item != null && !string.IsNullOrEmpty(item.FullText))
                        item.PreviewText = ClipboardItem.BuildPreviewText(item.FullText, maxChars);
                }
                ApplyCleanupRulesLocked();
                _storage.Save(_items);
            }

            HistoryChanged?.Invoke();
        }

        public void RecordText(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText)) return;
            if (_config != null && (!_config.Enabled || !_config.AutoRecord)) return;

            // 敏感信息过滤：必须在 MaxTextLength 截断之前按原始文本匹配，
            // 否则关键字落在被截掉的尾部会漏检。命中即整条丢弃：不入内存、不落盘、
            // 也不改动历史中此前已存在的同内容条目（过滤只影响此后的记录）。
            var filter = _filter;
            if (filter.IsActive)
            {
                ClipboardFilterRule hitRule;
                if (filter.TryMatch(rawText, out hitRule))
                {
                    RaiseContentFiltered(hitRule, rawText.Length);
                    return;
                }
            }

            // 超大文本先截断再入库，避免几十 MB 内容常驻内存并反复全量落盘（0 = 不限制）
            if (_config != null && _config.MaxTextLength > 0 && rawText.Length > _config.MaxTextLength)
            {
                rawText = rawText.Substring(0, _config.MaxTextLength);
            }

            string hash = ComputeHash(rawText);

            lock (_lock)
            {
                // 1. 防自环检查：若是刚刚双击选中的条目，抑制本次广播
                if (!string.IsNullOrEmpty(_suppressedHash))
                {
                    bool withinWindow = DateTime.Now <= _suppressedUntil;
                    bool matches = _suppressedHash == hash;
                    _suppressedHash = null;
                    _suppressedUntil = DateTime.MinValue;
                    if (withinWindow && matches) return;
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
                _suppressedUntil = DateTime.Now.AddSeconds(2);
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
                // UI 限制为 10 年；服务层仍防御手工编辑/旧坏配置，避免 AddDays 越界
                // 让整个剪贴板模块进入 Faulted。
                int retentionDays = Math.Min(_config.RetentionDays, 3650);
                var cutoff = DateTime.Now.AddDays(-retentionDays);
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

        /// <summary>
        /// 派发过滤命中事件。事件订阅方（模块层）只拿到规则与字符数，
        /// 不会拿到被丢弃的原文；订阅方自身抛异常也不得影响记录主流程。
        /// </summary>
        private void RaiseContentFiltered(ClipboardFilterRule rule, int textLength)
        {
            var handler = ContentFiltered;
            if (handler == null) return;

            try
            {
                handler(rule, textLength);
            }
            catch
            {
                // 日志/通知失败不应让敏感内容退回记录路径
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
