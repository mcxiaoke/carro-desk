using System;
using System.Collections.Generic;

namespace CarroDesk.Host.Commands
{
    /// <summary>
    /// 固定窗口限流（IPC 设计 §4.3 第 6 步）：按「能力名 + 来源」维度，每窗口最多 limit 次。
    /// 键空间 = 能力 × 来源，规模有界，不做窗口清理。
    /// 时钟可注入，保证限流单测的确定性。
    /// </summary>
    public sealed class CommandRateLimiter
    {
        private readonly object _lock = new object();
        private readonly int _limit;
        private readonly TimeSpan _window;
        private readonly Func<DateTime> _clock;
        private readonly Dictionary<string, WindowState> _windows =
            new Dictionary<string, WindowState>(StringComparer.Ordinal);

        private sealed class WindowState
        {
            public DateTime WindowStart;
            public int Count;
        }

        public CommandRateLimiter(int limitPerWindow = 30, TimeSpan? window = null, Func<DateTime> clock = null)
        {
            if (limitPerWindow <= 0) throw new ArgumentOutOfRangeException(nameof(limitPerWindow));
            _limit = limitPerWindow;
            _window = window ?? TimeSpan.FromMinutes(1);
            _clock = clock ?? (() => DateTime.UtcNow);
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
                    _windows[key] = new WindowState { WindowStart = now, Count = 1 };
                    return true;
                }
                if (state.Count >= _limit) return false;
                state.Count++;
                return true;
            }
        }
    }
}
