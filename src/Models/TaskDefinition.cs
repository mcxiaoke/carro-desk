using System;
using System.Collections.Generic;
using CarroDesk.Services.Localization;

namespace CarroDesk.Models
{
    public enum TaskTriggerType
    {
        Startup,
        Interval,
        Daily,
        Cron,
        SessionLock,
        SessionUnlock,
        Idle,
        Manual,
        Hotkey,
        Watch
    }

    public class TaskTrigger
    {
        public TaskTriggerType Type { get; set; } = TaskTriggerType.Startup;
        public int DelaySec { get; set; } = 5;
        public int EverySec { get; set; } = 0;
        public string Every { get; set; } = "";
        public string At { get; set; } = "";
        public string Expr { get; set; } = "";
        public int AfterMinutes { get; set; } = 0;
        public string Hotkey { get; set; } = "";
        public string WatchPath { get; set; } = "";
        public string WatchFilter { get; set; } = "*.*";
        public string WatchEvent { get; set; } = "created";

        public string RawType { get; set; } = "";
    }

    public class TaskCondition
    {
        public bool OnlyIdle { get; set; } = false;
        public bool AcPower { get; set; } = false;
        public string FileExists { get; set; } = "";
        public string FileNotExists { get; set; } = "";
        public bool NetworkAvailable { get; set; } = false;

        public bool HasAny()
        {
            return OnlyIdle || AcPower || !string.IsNullOrWhiteSpace(FileExists) || !string.IsNullOrWhiteSpace(FileNotExists) || NetworkAvailable;
        }
    }

    public class TaskAction
    {
        public string File { get; set; } = "";
        public string Args { get; set; } = "";
        public string WorkDir { get; set; } = "";
    }

    public class TaskOptions
    {
        public bool Hidden { get; set; } = true;
        public int TimeoutSec { get; set; } = 0;
        public bool AllowConcurrent { get; set; } = false;
        public int Retry { get; set; } = 0;
        public string WorkDir { get; set; } = "";
        public bool NotifyOnFailure { get; set; } = true;
    }

    public class TaskDefinition
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public TaskTrigger Trigger { get; set; } = new TaskTrigger();
        public TaskAction Action { get; set; } = new TaskAction();
        public TaskOptions Options { get; set; } = new TaskOptions();
        public TaskCondition When { get; set; } = new TaskCondition();

        public string StatusIndicator
        {
            get { return Enabled ? "🟢" : "⚪"; }
        }

        public string StatusColor
        {
            get { return Enabled ? "#10B981" : "#9CA3AF"; }
        }

        public string TriggerBadge
        {
            get { return "[" + (Trigger != null ? (Trigger.RawType != "" ? Trigger.RawType : Trigger.Type.ToString().ToLowerInvariant()) : "unknown") + "]"; }
        }

        public string TriggerBadgeText
        {
            get
            {
                if (Trigger == null) return Loc.T("Tasks.BadgeUnknown", "未知");
                switch (Trigger.Type)
                {
                    case TaskTriggerType.Startup: return Loc.T("Tasks.BadgeStartup", "启动");
                    case TaskTriggerType.Interval: return Loc.T("Tasks.BadgeInterval", "间隔");
                    case TaskTriggerType.Daily: return Loc.T("Tasks.BadgeDaily", "定时");
                    case TaskTriggerType.Cron: return Loc.T("Tasks.BadgeCron", "Cron");
                    case TaskTriggerType.SessionLock: return Loc.T("Tasks.BadgeSessionLock", "锁屏");
                    case TaskTriggerType.SessionUnlock: return Loc.T("Tasks.BadgeSessionUnlock", "解锁");
                    case TaskTriggerType.Idle: return Loc.T("Tasks.BadgeIdle", "空闲");
                    case TaskTriggerType.Manual: return Loc.T("Tasks.BadgeManual", "手动");
                    case TaskTriggerType.Hotkey: return Loc.T("Tasks.BadgeHotkey", "热键");
                    case TaskTriggerType.Watch: return Loc.T("Tasks.BadgeWatch", "监听");
                    default: return Trigger.Type.ToString();
                }
            }
        }

        public string TriggerBadgeBg
        {
            get
            {
                if (Trigger == null) return "#F3F4F6";
                switch (Trigger.Type)
                {
                    case TaskTriggerType.Startup: return "#EEF2FF";
                    case TaskTriggerType.Interval: return "#ECFDF5";
                    case TaskTriggerType.Daily: return "#F5F3FF";
                    case TaskTriggerType.Cron: return "#F3E8FF";
                    case TaskTriggerType.SessionLock: return "#FFF7ED";
                    case TaskTriggerType.SessionUnlock: return "#FEF3C7";
                    case TaskTriggerType.Idle: return "#FEF9C3";
                    case TaskTriggerType.Manual: return "#F1F5F9";
                    case TaskTriggerType.Hotkey: return "#F3F4F6";
                    case TaskTriggerType.Watch: return "#ECFEFF";
                    default: return "#F3F4F6";
                }
            }
        }

        public string TriggerBadgeFg
        {
            get
            {
                if (Trigger == null) return "#6B7280";
                switch (Trigger.Type)
                {
                    case TaskTriggerType.Startup: return "#4338CA";
                    case TaskTriggerType.Interval: return "#047857";
                    case TaskTriggerType.Daily: return "#6D28D9";
                    case TaskTriggerType.Cron: return "#7E22CE";
                    case TaskTriggerType.SessionLock: return "#C2410C";
                    case TaskTriggerType.SessionUnlock: return "#B45309";
                    case TaskTriggerType.Idle: return "#A16207";
                    case TaskTriggerType.Manual: return "#475569";
                    case TaskTriggerType.Hotkey: return "#374151";
                    case TaskTriggerType.Watch: return "#0E7490";
                    default: return "#6B7280";
                }
            }
        }

        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(Name)) return "name required";
            if (Name.Length > 64) return "name too long";
            foreach (char c in Name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok) return "name invalid char: " + c;
            }
            if (Trigger == null) return "trigger required";
            if (Action == null || string.IsNullOrWhiteSpace(Action.File)) return "action.file required";
            if (Trigger.Type == TaskTriggerType.Interval)
            {
                int sec = Trigger.EverySec;
                if (sec <= 0 && !string.IsNullOrWhiteSpace(Trigger.Every))
                    sec = ParseDuration(Trigger.Every);
                if (sec <= 0) return "interval everySec/every required and >0";
            }
            if (Trigger.Type == TaskTriggerType.Daily)
            {
                if (string.IsNullOrWhiteSpace(Trigger.At)) return "daily at required (HH:mm)";
                TimeSpan t;
                if (!TryParseTime(Trigger.At, out t)) return "daily at invalid: " + Trigger.At;
            }
            if (Trigger.Type == TaskTriggerType.Cron)
            {
                if (string.IsNullOrWhiteSpace(Trigger.Expr)) return "cron expr required";
                string err;
                if (!CronHelper.Validate(Trigger.Expr, out err)) return "cron invalid: " + err;
            }
            if (Trigger.Type == TaskTriggerType.Idle)
            {
                if (Trigger.AfterMinutes <= 0) return "idle afterMinutes required and >0";
            }
            if (Trigger.Type == TaskTriggerType.Hotkey)
            {
                if (string.IsNullOrWhiteSpace(Trigger.Hotkey)) return "hotkey required (e.g. Ctrl+Alt+Q)";
                string err;
                if (!HotkeyHelper.Validate(Trigger.Hotkey, out err)) return "hotkey invalid: " + err;
            }
            if (Trigger.Type == TaskTriggerType.Watch)
            {
                if (string.IsNullOrWhiteSpace(Trigger.WatchPath)) return "watch path required";
            }
            if (Options != null && Options.TimeoutSec < 0) return "timeoutSec invalid";
            if (Options != null && Options.Retry < 0) return "retry invalid";
            return null;
        }

        public static int ParseDuration(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            text = text.Trim().ToLowerInvariant();
            int total = 0;
            int num = 0;
            bool hasNum = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= '0' && c <= '9')
                {
                    num = num * 10 + (c - '0');
                    hasNum = true;
                }
                else if (c == 'h')
                {
                    if (!hasNum) return 0;
                    total += num * 3600;
                    num = 0; hasNum = false;
                }
                else if (c == 'm')
                {
                    if (!hasNum) return 0;
                    // check for "ms" ? not needed
                    total += num * 60;
                    num = 0; hasNum = false;
                }
                else if (c == 's')
                {
                    if (!hasNum) return 0;
                    total += num;
                    num = 0; hasNum = false;
                }
                else if (c == ' ' || c == '\t')
                {
                    continue;
                }
                else
                {
                    return 0;
                }
            }
            if (hasNum) total += num; // bare number as seconds
            return total;
        }

        public static bool TryParseTime(string text, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            string[] parts = text.Split(':');
            if (parts.Length < 2 || parts.Length > 3) return false;
            int h, m, s = 0;
            if (!int.TryParse(parts[0], out h)) return false;
            if (!int.TryParse(parts[1], out m)) return false;
            if (parts.Length == 3 && !int.TryParse(parts[2], out s)) return false;
            if (h < 0 || h > 23) return false;
            if (m < 0 || m > 59) return false;
            if (s < 0 || s > 59) return false;
            time = new TimeSpan(h, m, s);
            return true;
        }

        public int GetIntervalSeconds()
        {
            if (Trigger == null) return 0;
            if (Trigger.EverySec > 0) return Trigger.EverySec;
            if (!string.IsNullOrWhiteSpace(Trigger.Every)) return ParseDuration(Trigger.Every);
            return 0;
        }

        public string EffectiveWorkDir()
        {
            if (Options != null && !string.IsNullOrWhiteSpace(Options.WorkDir)) return Options.WorkDir;
            if (Action != null && !string.IsNullOrWhiteSpace(Action.WorkDir)) return Action.WorkDir;
            if (Action != null && !string.IsNullOrWhiteSpace(Action.File))
            {
                try { return System.IO.Path.GetDirectoryName(Action.File); } catch { }
            }
            return "";
        }
    }

    public static class CronHelper
    {
        public static bool Validate(string expr, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(expr)) { error = "empty"; return false; }
            var parts = expr.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5) { error = "need 5 fields (m h dom mon dow)"; return false; }
            // minute 0-59, hour 0-23, dom 1-31, mon 1-12, dow 0-7 (0/7 Sunday)
            int[][] ranges = new int[][] { new int[]{0,59}, new int[]{0,23}, new int[]{1,31}, new int[]{1,12}, new int[]{0,7} };
            for (int i = 0; i < 5; i++)
            {
                string e;
                if (!ValidateField(parts[i], ranges[i][0], ranges[i][1], out e)) { error = "field " + (i+1) + ": " + e; return false; }
            }
            return true;
        }

        private static bool ValidateField(string field, int min, int max, out string error)
        {
            error = null;
            if (field == "*" || field == "?") return true;
            var tokens = field.Split(',');
            foreach (var token in tokens)
            {
                string t = token.Trim();
                if (string.IsNullOrEmpty(t)) { error = "empty token"; return false; }
                string rangePart = t;
                string stepPart = null;
                int slash = t.IndexOf('/');
                if (slash >= 0)
                {
                    rangePart = t.Substring(0, slash);
                    stepPart = t.Substring(slash + 1);
                    int step;
                    if (!int.TryParse(stepPart, out step) || step <= 0) { error = "bad step " + stepPart; return false; }
                }
                if (rangePart == "*" || rangePart == "") continue;
                if (rangePart.Contains("-"))
                {
                    var rp = rangePart.Split('-');
                    if (rp.Length != 2) { error = "bad range " + rangePart; return false; }
                    int a,b;
                    if (!int.TryParse(rp[0], out a) || !int.TryParse(rp[1], out b)) { error = "bad range number"; return false; }
                    if (a < min || b > max || a > b) { error = "range out of bounds"; return false; }
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

        public static bool IsMatch(DateTime dt, string expr)
        {
            var parts = expr.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5) return false;
            int[] vals = new int[] { dt.Minute, dt.Hour, dt.Day, dt.Month, (int)dt.DayOfWeek };
            // cron dow: 0 and 7 both Sunday
            if (vals[4] == 0) { /* keep 0 */ }
            int[][] ranges = new int[][] { new int[]{0,59}, new int[]{0,23}, new int[]{1,31}, new int[]{1,12}, new int[]{0,7} };
            for (int i = 0; i < 5; i++)
            {
                if (!FieldMatches(parts[i], vals[i], ranges[i][0], ranges[i][1])) return false;
            }
            return true;
        }

        private static bool FieldMatches(string field, int value, int min, int max)
        {
            if (field == "*" || field == "?") return true;
            var tokens = field.Split(',');
            foreach (var token in tokens)
            {
                string t = token.Trim();
                string rangePart = t;
                int step = 1;
                int slash = t.IndexOf('/');
                if (slash >= 0)
                {
                    rangePart = t.Substring(0, slash);
                    int.TryParse(t.Substring(slash + 1), out step);
                    if (step <= 0) step = 1;
                }
                if (rangePart == "*" || rangePart == "")
                {
                    if ((value - min) % step == 0) return true;
                    continue;
                }
                if (rangePart.Contains("-"))
                {
                    var rp = rangePart.Split('-');
                    int a = int.Parse(rp[0]);
                    int b = int.Parse(rp[1]);
                    if (value >= a && value <= b && ((value - a) % step == 0)) return true;
                }
                else
                {
                    int v = int.Parse(rangePart);
                    // handle dow 7 == 0
                    if (max == 7 && v == 7) v = 0;
                    int cmp = value;
                    if (max == 7 && cmp == 7) cmp = 0;
                    if (cmp == v) return true;
                }
            }
            return false;
        }

        public static DateTime? GetNextOccurrence(string expr, DateTime fromTime)
        {
            string err;
            if (!Validate(expr, out err)) return null;
            var cur = new DateTime(fromTime.Year, fromTime.Month, fromTime.Day, fromTime.Hour, fromTime.Minute, 0).AddMinutes(1);
            int maxMinutes = 60 * 24 * 366;
            for (int i = 0; i < maxMinutes; i++)
            {
                if (IsMatch(cur, expr)) return cur;
                cur = cur.AddMinutes(1);
            }
            return null;
        }

        public static string ExplainCron(string expr)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(expr)) return "未填写表达式";
                var parts = expr.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 5) return "需 5 个字段 (分 时 日 月 周)";
                string m = parts[0], h = parts[1], dom = parts[2], mon = parts[3], dow = parts[4];

                if (m == "*" && h == "*" && dom == "*" && mon == "*" && dow == "*") return "每分钟执行一次";
                if (m.StartsWith("*/") && h == "*") return string.Format("每隔 {0} 分钟执行一次", m.Substring(2));
                if (m.StartsWith("*/") && h != "*") return string.Format("在指定小时 ({0}) 内每隔 {1} 分钟执行", h, m.Substring(2));
                if (h.StartsWith("*/")) return string.Format("每隔 {0} 小时的第 {1} 分钟执行", h.Substring(2), m);
                if (dom == "*" && mon == "*" && dow == "*")
                {
                    int ih, im;
                    if (int.TryParse(h, out ih) && int.TryParse(m, out im))
                        return string.Format("每天 {0:D2}:{1:D2} 执行", ih, im);
                }
                if (dom == "*" && mon == "*" && dow != "*")
                {
                    string dowText = dow;
                    if (dow == "1-5") dowText = "工作日 (周一至周五)";
                    else if (dow == "0,6" || dow == "6,0" || dow == "6-7") dowText = "周末 (周六和周日)";
                    else dowText = "周 " + dow;
                    int ih, im;
                    if (int.TryParse(h, out ih) && int.TryParse(m, out im))
                        return string.Format("每{0} {1:D2}:{2:D2} 执行", dowText, ih, im);
                }
                if (dom != "*" && mon == "*" && dow == "*")
                {
                    int ih, im;
                    if (int.TryParse(h, out ih) && int.TryParse(m, out im))
                        return string.Format("每月 {0} 号 {1:D2}:{2:D2} 执行", dom, ih, im);
                }
                return "按规则执行: " + expr;
            }
            catch { return "有效表达式: " + expr; }
        }
    }

    internal static class HotkeyHelper
    {
        public static bool Validate(string hotkey, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(hotkey)) { error = "empty"; return false; }
            int mods;
            int vk;
            if (!TryParse(hotkey, out mods, out vk, out error)) return false;
            if (vk == 0) { error = "no key"; return false; }
            return true;
        }

        public static bool TryParse(string hotkey, out int mods, out int vk, out string error)
        {
            mods = 0; vk = 0; error = null;
            if (string.IsNullOrWhiteSpace(hotkey)) { error = "empty"; return false; }
            // format: Ctrl+Alt+Shift+Win+Key  (case insensitive, + or - separator)
            string s = hotkey.Trim();
            s = s.Replace("-", "+");
            var parts = s.Split(new char[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { error = "empty"; return false; }
            string keyPart = parts[parts.Length - 1].Trim();
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string m = parts[i].Trim().ToLowerInvariant();
                if (m == "ctrl" || m == "control") mods |= 0x0002;
                else if (m == "alt") mods |= 0x0001;
                else if (m == "shift") mods |= 0x0004;
                else if (m == "win" || m == "windows" || m == "meta") mods |= 0x0008;
                else { error = "unknown modifier " + parts[i]; return false; }
            }
            // key: single char A-Z, 0-9, F1-24, or names like Space, Enter, Esc
            string k = keyPart.ToUpperInvariant();
            if (k.Length == 1)
            {
                char c = k[0];
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    vk = (int)c;
                    return true;
                }
                switch (c)
                {
                    case '`': case '~': vk = 0xC0; return true; // VK_OEM_3
                    case '-': case '_': vk = 0xBD; return true; // VK_OEM_MINUS
                    case '=': case '+': vk = 0xBB; return true; // VK_OEM_PLUS
                    case '[': case '{': vk = 0xDB; return true; // VK_OEM_4
                    case ']': case '}': vk = 0xDD; return true; // VK_OEM_6
                    case '\\': case '|': vk = 0xDC; return true; // VK_OEM_5
                    case ';': case ':': vk = 0xBA; return true; // VK_OEM_1
                    case '\'': case '"': vk = 0xDE; return true; // VK_OEM_7
                    case ',': case '<': vk = 0xBC; return true; // VK_OEM_COMMA
                    case '.': case '>': vk = 0xBE; return true; // VK_OEM_PERIOD
                    case '/': case '?': vk = 0xBF; return true; // VK_OEM_2
                }
            }
            // try function keys
            if (k.StartsWith("F"))
            {
                int fn;
                if (int.TryParse(k.Substring(1), out fn) && fn >= 1 && fn <= 24)
                {
                    vk = 0x70 + (fn - 1); // VK_F1=0x70
                    return true;
                }
            }
            // named keys
            switch (k)
            {
                case "`": case "~": case "GRAVE": case "BACKQUOTE": case "TILDE": case "OEM3": vk = 0xC0; return true;
                case "MINUS": case "DASH": vk = 0xBD; return true;
                case "PLUS": case "EQUAL": case "EQUALS": vk = 0xBB; return true;
                case "SPACE": vk = 0x20; return true;
                case "ENTER": case "RETURN": vk = 0x0D; return true;
                case "ESC": case "ESCAPE": vk = 0x1B; return true;
                case "TAB": vk = 0x09; return true;
                case "BACKSPACE": case "BACK": vk = 0x08; return true;
                case "INS": case "INSERT": vk = 0x2D; return true;
                case "DEL": case "DELETE": vk = 0x2E; return true;
                case "HOME": vk = 0x24; return true;
                case "END": vk = 0x23; return true;
                case "PGUP": case "PAGEUP": vk = 0x21; return true;
                case "PGDN": case "PAGEDOWN": vk = 0x22; return true;
                case "LEFT": vk = 0x25; return true;
                case "UP": vk = 0x26; return true;
                case "RIGHT": vk = 0x27; return true;
                case "DOWN": vk = 0x28; return true;
                default: error = "unknown key " + keyPart; return false;
            }
        }
    }
}
