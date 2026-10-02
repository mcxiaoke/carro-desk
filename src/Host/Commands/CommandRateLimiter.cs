using System;
using System.Collections.Generic;

namespace CarroDesk.Host.Commands
{
    /// <summary>
    /// 固定窗口限流（IPC 设计 §4.3 第 6 步）：按「能力名 + 来源」维度，每窗口最多 limit 次。
    /// 来源在协议解析层已被归一化到封闭集合（见 RpcProtocol.NormalizeSource），
    /// 因此键空间 = 能力数 × 固定来源数，规模有界；此处仍保留容量上限作为兜底，
    /// 防止未来新增能力或来源枚举时再次出现无界增长。
    /// 时钟可注入，保证限流单测的确定性。
    /// </summary>
    public sealed class CommandRateLimiter
    {
        private readonly object _lock = new object();
        private readonly int _limit;
        private readonly TimeSpan _window;
        private readonly Func<DateTime> _clock;
        private readonly int _maxEntries;
        private readonly Dictionary<string, WindowState> _windows =
            new Dictionary<string, WindowState>(StringComparer.Ordinal);

        private sealed class WindowState
        {
            public DateTime WindowStart;
            public int Count;
        }

        public CommandRateLimiter(int limitPerWindow = 30, TimeSpan? window = null, Func<DateTime> clock = null,
            int maxEntries = 4096)
        {
            if (limitPerWindow <= 0) throw new ArgumentOutOfRangeException(nameof(limitPerWindow));
            _limit = limitPerWindow;
            _window = window ?? TimeSpan.FromMinutes(1);
            _clock = clock ?? (() => DateTime.UtcNow);
            _maxEntries = maxEntries > 0 ? maxEntries : 4096;
        }

        /// <summary>true = 放行；false = 当前窗口已超限。</summary>
        public bool TryAcquire(string method, string source)
        {
            var now = _clock();
            var key = (method ?? string.Empty) + "|" + (source ?? string.Empty);
            lock (_lock)
            {
                WindowState state;
                if (!_windows.TryGetValue(key, out state) || now - state.WindowStart >= _window)
                {
                    EvictExpiredNoLock(now);
                    // 仍超出容量时整表清空：窗口状态本就是短周期数据，
                    // 清空的代价是让若干条调用方获得一次额外额度，
                    // 相比无界增长的内存风险可以接受。
                    if (_windows.Count >= _maxEntries) _windows.Clear();
                    _windows[key] = new WindowState { WindowStart = now, Count = 1 };
                    return true;
                }
                if (state.Count >= _limit) return false;
                state.Count++;
                return true;
            }
        }

        /// <summary>清除所有已过期的窗口条目，仅在容量压力下调用，避免每次请求都全表扫描。</summary>
        private void EvictExpiredNoLock(DateTime now)
        {
            if (_windows.Count < _maxEntries) return;
            List<string> expired = null;
            foreach (var pair in _windows)
            {
                if (now - pair.Value.WindowStart >= _window)
                {
                    (expired ?? (expired = new List<string>())).Add(pair.Key);
                }
            }
            if (expired == null) return;
            foreach (var key in expired) _windows.Remove(key);
        }
    }
}
