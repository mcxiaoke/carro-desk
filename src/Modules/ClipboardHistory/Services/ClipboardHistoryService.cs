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
        /// <summary>内存中所有条目 FullText 的 UTF-8 字节数合计，用于总预算淘汰（H8）。</summary>
        private long _totalBytes;
        // 配置重载（后台/IPC）与 RecordText（UI 线程）并发，引用写入必须可见。
        private volatile ClipboardHistoryConfig _config;
        private string _suppressedHash;

        /// <summary>可选日志出口（模块层注入），不设置时静默；不弹通知打扰用户。</summary>
        public Action<string> LogWarning { get; set; }
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
                _totalBytes = 0;
                if (loaded != null && loaded.Count > 0)
                {
                    _items.AddRange(loaded);
                    foreach (var item in _items) _totalBytes += ByteSize(item);
                    ApplyCleanupRulesLocked();
                }
                // 保持既有语义：启动/配置变更不自动改写历史（避免"配置一变历史就没了"）。
                // 需要清除历史中已存在的敏感内容时，由用户显式调用 PurgeSensitiveHistory()。
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

            // 先消费自环抑制（M18）：若程序自己写入的内容恰好命中敏感过滤，
            // 旧实现会在过滤分支提前 return，抑制哈希残留到 2 秒窗口结束，
            // 导致用户随后真实复制相同内容被误判为自环而丢弃。
            if (ConsumeSelfEcho(rawText)) return;

            // 敏感信息过滤：必须在 MaxTextLength 截断之前按原始文本匹配，
            // 否则关键字落在被截掉的尾部会漏检。命中即整条丢弃：不入内存、不落盘。
            // 本路径只拦新记录；历史中已存在条目由用户显式调用 PurgeSensitiveHistory()
            // 清理（不自动回溯，避免配置变更意外删掉历史）。
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
                // 1. 去重与 MRU 提升：若已有该条目，则刷新时间戳并移到顶端
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
                    _totalBytes += ByteSize(item);
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

        /// <summary>
        /// 若本次文本正是程序刚写入剪贴板的内容（2 秒窗口内），消费抑制并返回 true。
        /// 无论是否命中窗口，只要存在抑制状态就一并清空，避免陈旧哈希影响后续真实复制。
        /// </summary>
        private bool ConsumeSelfEcho(string rawText)
        {
            // 快速路径：没有抑制时不做哈希（绝大多数复制都走这里）
            if (string.IsNullOrEmpty(_suppressedHash)) return false;

            string hash = ComputeHash(rawText);
            lock (_lock)
            {
                if (string.IsNullOrEmpty(_suppressedHash)) return false;
                bool withinWindow = DateTime.Now <= _suppressedUntil;
                bool matches = _suppressedHash == hash;
                _suppressedHash = null;
                _suppressedUntil = DateTime.MinValue;
                return withinWindow && matches;
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
                    RemoveWhereLocked(x => !x.IsPinned);
                }
                else
                {
                    _items.Clear();
                    _totalBytes = 0;
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
                RemoveWhereLocked(x => x.Id == id);
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

        /// <summary>
        /// 显式清除历史中命中当前过滤规则的条目（用户从托盘主动触发）。
        /// 刻意不做自动回溯：既有语义是"过滤只影响此后的记录"，配置一变就删历史
        /// 会让用户意外丢数据；给用户一个可发现的一次性入口即可。
        /// 返回删除条数，删除后立即落盘。
        /// </summary>
        public int PurgeSensitiveHistory()
        {
            lock (_lock)
            {
                int removed = PurgeSensitiveHistoryLocked();
                if (removed > 0) _storage.Save(_items);
                return removed;
            }
        }

        /// <summary>在持有 _lock 时按当前规则清除命中项（含置顶项：敏感内容优先于置顶语义）。</summary>
        private int PurgeSensitiveHistoryLocked()
        {
            var filter = _filter;
            if (filter == null || !filter.IsActive) return 0;

            int removed = RemoveWhereLocked(item =>
            {
                if (item == null || string.IsNullOrEmpty(item.FullText)) return false;
                ClipboardFilterRule hit;
                return filter.TryMatch(item.FullText, out hit);
            });

            if (removed > 0)
            {
                LogWarning?.Invoke("敏感过滤规则变更：从剪贴板历史中清除 " + removed + " 条命中记录");
            }
            return removed;
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
                RemoveWhereLocked(x => !x.IsPinned && x.CopiedAt < cutoff);
            }

            // 规则 2：最大条数截断（保留最新，从最末尾倒序淘汰未置顶的最老项）
            if (_config.MaxItems > 0 && _items.Count > _config.MaxItems)
            {
                for (int i = _items.Count - 1; i >= 0 && _items.Count > _config.MaxItems; i--)
                {
                    if (_items[i].IsPinned) continue;
                    RemoveAtLocked(i);
                }

                // 极端情况：若置顶项本身就超过 MaxItems，直接截断
                for (int i = _items.Count - 1; i >= _config.MaxItems; i--)
                {
                    RemoveAtLocked(i);
                }
            }

            // 规则 3：总字节预算（H8）。从最旧的未置顶条目倒序淘汰；
            // 置顶项受用户显式保护，即使超预算也保留。
            if (_config.MaxTotalBytes > 0)
            {
                for (int i = _items.Count - 1; i >= 0 && _totalBytes > _config.MaxTotalBytes; i--)
                {
                    if (_items[i].IsPinned) continue;
                    RemoveAtLocked(i);
                }
            }
        }

        private static long ByteSize(ClipboardItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FullText)) return 0;
            return Encoding.UTF8.GetByteCount(item.FullText);
        }

        private void RemoveAtLocked(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            _totalBytes -= ByteSize(_items[index]);
            if (_totalBytes < 0) _totalBytes = 0;
            _items.RemoveAt(index);
        }

        private int RemoveWhereLocked(Func<ClipboardItem, bool> predicate)
        {
            int removed = 0;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (!predicate(_items[i])) continue;
                RemoveAtLocked(i);
                removed++;
            }
            return removed;
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
