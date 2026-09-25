using System;
using System.Collections.Concurrent;
using System.Text;
using CarroDesk.Services.Localization;

namespace CarroDesk.Services.Tasks
{
    /// <summary>
    /// 已编译的 Cron 表达式位掩码结构，支持极速位运算匹配与维度跳跃。
    /// </summary>
    public sealed class CompiledCronExpression
    {
        public long MinuteMask { get; }      // 0..59 -> 60 bits
        public int HourMask { get; }         // 0..23 -> 24 bits
        public int DayOfMonthMask { get; }   // 1..31 -> 32 bits
        public int MonthMask { get; }        // 1..12 -> 13 bits
        public int DayOfWeekMask { get; }    // 0..6  -> 7 bits (0/7 are Sunday)

        /// <summary>DayOfMonth 字段是否被限定（非 * / ?）。用于还原标准 cron 的 DOM/DOW 组合语义。</summary>
        public bool DayOfMonthRestricted { get; }

        /// <summary>DayOfWeek 字段是否被限定（非 * / ?）。</summary>
        public bool DayOfWeekRestricted { get; }

        public CompiledCronExpression(long min, int hr, int dom, int mon, int dow,
            bool dayOfMonthRestricted, bool dayOfWeekRestricted)
        {
            MinuteMask = min;
            HourMask = hr;
            DayOfMonthMask = dom;
            MonthMask = mon;
            DayOfWeekMask = dow;
            DayOfMonthRestricted = dayOfMonthRestricted;
            DayOfWeekRestricted = dayOfWeekRestricted;
        }

        /// <summary>
        /// 日期匹配。
        /// 标准 cron 语义：DOM 与 DOW **同时**被限定时取"或"，只限定其中一个时取"与"
        /// （未限定的字段掩码为全 1，因此"与"形式天然退化为只判断被限定的那个）。
        /// 例如 `0 9 1 * 1` 表示"每月 1 号**或**每周一 09:00"，原实现恒用"与"，
        /// 变成只在"1 号且是周一"触发，导致用户配置静默失效。
        /// </summary>
        public bool IsDayMatch(DateTime dt)
        {
            bool domMatch = ((1 << dt.Day) & DayOfMonthMask) != 0;
            bool dowMatch = ((1 << (int)dt.DayOfWeek) & DayOfWeekMask) != 0;

            if (DayOfMonthRestricted && DayOfWeekRestricted) return domMatch || dowMatch;
            return domMatch && dowMatch;
        }

        public bool IsMatch(DateTime dt)
        {
            if (((1L << dt.Minute) & MinuteMask) == 0) return false;
            if (((1 << dt.Hour) & HourMask) == 0) return false;
            if (((1 << dt.Month) & MonthMask) == 0) return false;
            if (!IsDayMatch(dt)) return false;
            return true;
        }
    }

    public static class CronHelper
    {
        /// <summary>编译结果缓存上限（防御性，正常配置远小于此值）。</summary>
        private const int MaxCacheEntries = 256;

        private static readonly ConcurrentDictionary<string, CompiledCronExpression> _cache =
            new ConcurrentDictionary<string, CompiledCronExpression>(StringComparer.Ordinal);

        public static bool Validate(string expr, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(expr)) { error = "empty"; return false; }
            var parts = expr.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5) { error = "need 5 fields (m h dom mon dow)"; return false; }

            int[][] ranges = new int[][]
            {
                new int[] { 0, 59 },
                new int[] { 0, 23 },
                new int[] { 1, 31 },
                new int[] { 1, 12 },
                new int[] { 0, 7 }
            };

            for (int i = 0; i < 5; i++)
            {
                string e;
                if (!ValidateField(parts[i], ranges[i][0], ranges[i][1], out e))
                {
                    error = "field " + (i + 1) + ": " + e;
                    return false;
                }
            }
            return true;
        }

        private static bool ValidateField(string field, int min, int max, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(field)) { error = "empty"; return false; }
            if (field == "*" || field == "?") return true;

            var tokens = field.Split(',');
            if (tokens.Length == 0) { error = "empty"; return false; }

            foreach (var token in tokens)
            {
                string t = token.Trim();
                if (string.IsNullOrEmpty(t)) { error = "empty token"; return false; }

                string rangePart = t;
                if (t.Contains("/"))
                {
                    var slash = t.Split('/');
                    if (slash.Length != 2) { error = "invalid step " + t; return false; }
                    rangePart = slash[0];
                    int step;
                    if (!int.TryParse(slash[1], out step) || step <= 0) { error = "invalid step " + slash[1]; return false; }
                }

                if (rangePart == "*" || rangePart == "") continue;

                if (rangePart.Contains("-"))
                {
                    var dash = rangePart.Split('-');
                    if (dash.Length != 2) { error = "invalid range " + rangePart; return false; }
                    int a, b;
                    if (!int.TryParse(dash[0], out a) || !int.TryParse(dash[1], out b)) { error = "bad range numbers"; return false; }
                    if (a < min || a > max || b < min || b > max) { error = "range out of bounds"; return false; }
                    if (a > b) { error = "range start > end"; return false; }
                }
                else
                {
                    int v;
                    if (!int.TryParse(rangePart, out v)) { error = "bad value " + rangePart; return false; }
                    if (v < min || v > max) { error = "value out of range"; return false; }
                }
            }
            return true;
        }

        public static CompiledCronExpression Compile(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr)) return null;

            string key = expr.Trim();
            CompiledCronExpression cached;
            if (_cache.TryGetValue(key, out cached)) return cached;

            var compiled = Build(key);
            if (compiled == null) return null;

            // 简单容量上限：表达式来自用户配置，正常数量有限，
            // 这里只是防御异常输入造成的缓存无界增长。
            if (_cache.Count >= MaxCacheEntries) _cache.Clear();
            _cache[key] = compiled;
            return compiled;
        }

        private static CompiledCronExpression Build(string expr)
        {
            string err;
            if (!Validate(expr, out err)) return null;
            var parts = expr.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5) return null;

            long minMask = ParseFieldMask64(parts[0], 0, 59);
            int hrMask = (int)ParseFieldMask64(parts[1], 0, 23);
            int domMask = (int)ParseFieldMask64(parts[2], 1, 31);
            int monMask = (int)ParseFieldMask64(parts[3], 1, 12);
            int dowMask = ParseDowMask(parts[4]);

            bool domRestricted = IsRestricted(parts[2]);
            bool dowRestricted = IsRestricted(parts[4]);

            return new CompiledCronExpression(minMask, hrMask, domMask, monMask, dowMask, domRestricted, dowRestricted);
        }

        private static bool IsRestricted(string field)
        {
            return !string.Equals(field, "*", StringComparison.Ordinal) &&
                   !string.Equals(field, "?", StringComparison.Ordinal);
        }

        private static long ParseFieldMask64(string field, int min, int max)
        {
            if (field == "*" || field == "?")
            {
                long mask = 0;
                for (int i = min; i <= max; i++) mask |= (1L << i);
                return mask;
            }

            long result = 0;
            var tokens = field.Split(',');
            foreach (var token in tokens)
            {
                string t = token.Trim();
                int step = 1;
                string rangePart = t;
                int slash = t.IndexOf('/');
                if (slash >= 0)
                {
                    rangePart = t.Substring(0, slash);
                    int.TryParse(t.Substring(slash + 1), out step);
                    if (step <= 0) step = 1;
                }

                int start = min;
                int end = max;
                if (rangePart != "*" && rangePart != "")
                {
                    if (rangePart.Contains("-"))
                    {
                        var rp = rangePart.Split('-');
                        start = int.Parse(rp[0]);
                        end = int.Parse(rp[1]);
                    }
                    else
                    {
                        start = int.Parse(rangePart);
                        end = start;
                    }
                }

                for (int v = start; v <= end; v += step)
                {
                    if (v >= min && v <= max) result |= (1L << v);
                }
            }
            return result;
        }

        private static int ParseDowMask(string field)
        {
            if (field == "*" || field == "?")
            {
                int mask = 0;
                for (int i = 0; i <= 6; i++) mask |= (1 << i);
                return mask;
            }

            int result = 0;
            var tokens = field.Split(',');
            foreach (var token in tokens)
            {
                string t = token.Trim();
                int step = 1;
                string rangePart = t;
                int slash = t.IndexOf('/');
                if (slash >= 0)
                {
                    rangePart = t.Substring(0, slash);
                    int.TryParse(t.Substring(slash + 1), out step);
                    if (step <= 0) step = 1;
                }

                int start = 0;
                int end = 7;
                if (rangePart != "*" && rangePart != "")
                {
                    if (rangePart.Contains("-"))
                    {
                        var rp = rangePart.Split('-');
                        start = int.Parse(rp[0]);
                        end = int.Parse(rp[1]);
                    }
                    else
                    {
                        start = int.Parse(rangePart);
                        end = start;
                    }
                }

                for (int v = start; v <= end; v += step)
                {
                    int day = v;
                    if (day == 7) day = 0; // 0 and 7 are Sunday
                    if (day >= 0 && day <= 6) result |= (1 << day);
                }
            }
            return result;
        }

        public static bool IsMatch(DateTime dt, string expr)
        {
            var compiled = Compile(expr);
            return compiled != null && compiled.IsMatch(dt);
        }

        public static DateTime? GetNextOccurrence(string expr, DateTime fromTime)
        {
            var compiled = Compile(expr);
            if (compiled == null) return null;

            var cur = new DateTime(fromTime.Year, fromTime.Month, fromTime.Day, fromTime.Hour, fromTime.Minute, 0).AddMinutes(1);
            // 需覆盖 2 月 29 日（相邻两次最长可相隔四年）；八年也可识别常见永不可达日期。
            DateTime limit = cur.AddYears(8);

            while (cur < limit)
            {
                // 1. 月份不匹配 -> 直接跳跃至下月 1 日 00:00
                if (((1 << cur.Month) & compiled.MonthMask) == 0)
                {
                    cur = new DateTime(cur.Year, cur.Month, 1, 0, 0, 0).AddMonths(1);
                    continue;
                }

                // 2. 日期或星期不匹配 -> 直接跳跃至次日 00:00
                if (!compiled.IsDayMatch(cur))
                {
                    cur = cur.Date.AddDays(1);
                    continue;
                }

                // 3. 小时不匹配 -> 直接跳跃至下一小时 00 分
                if (((1 << cur.Hour) & compiled.HourMask) == 0)
                {
                    cur = new DateTime(cur.Year, cur.Month, cur.Day, cur.Hour, 0, 0).AddHours(1);
                    continue;
                }

                // 4. 分钟匹配判定
                if (((1L << cur.Minute) & compiled.MinuteMask) != 0)
                {
                    return cur;
                }

                cur = cur.AddMinutes(1);
            }

            return null;
        }

        public static string ExplainCron(string expr)
        {
            try
            {
                string err;
                if (!Validate(expr, out err)) return Loc.T("Cron.Invalid", "表达式无效: {0}", err);
                var parts = expr.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string m = parts[0], h = parts[1], dom = parts[2], mon = parts[3], dow = parts[4];

                if (expr.Trim() == "* * * * *") return Loc.T("Cron.EveryMinute", "每分钟执行一次");
                if (m.StartsWith("*/") && h == "*" && dom == "*" && mon == "*" && dow == "*")
                    return Loc.T("Cron.EveryNMinutes", "每隔 {0} 分钟执行一次", m.Substring(2));

                int im, ih;
                bool mIsNum = int.TryParse(m, out im);
                bool hIsNum = int.TryParse(h, out ih);

                if (mIsNum && hIsNum && dom == "*" && mon == "*")
                {
                    if (dow == "*") return Loc.T("Cron.Daily", "每天 {0:D2}:{1:D2} 执行", ih, im);
                    if (dow == "1-5") return Loc.T("Cron.Weekdays", "工作日 (周一至周五) {0:D2}:{1:D2} 执行", ih, im);
                    if (dow == "0,6" || dow == "6,0" || dow == "7,6" || dow == "6,7")
                        return Loc.T("Cron.Weekend", "周末 (周六周日) {0:D2}:{1:D2} 执行", ih, im);
                    if (dow == "1") return Loc.T("Cron.WeeklyMonday", "每周一 {0:D2}:{1:D2} 执行", ih, im);
                    if (dow == "0" || dow == "7") return Loc.T("Cron.WeeklySunday", "每周日 {0:D2}:{1:D2} 执行", ih, im);
                }

                if (mIsNum && hIsNum && mon == "*" && dow == "*" && int.TryParse(dom, out _))
                {
                    return Loc.T("Cron.Monthly", "每月 {0} 号 {1:D2}:{2:D2} 执行", dom, ih, im);
                }

                return Loc.T("Cron.ByRule", "按规则执行: {0}", expr);
            }
            catch
            {
                return Loc.T("Cron.ValidExpression", "有效表达式: {0}", expr);
            }
        }
    }
}
