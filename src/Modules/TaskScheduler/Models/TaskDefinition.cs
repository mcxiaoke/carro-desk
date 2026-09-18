using System;
using System.Collections.Generic;
using CarroDesk.Services.Localization;
using CarroDesk.Services.Tasks;
using Newtonsoft.Json;

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

        [JsonIgnore]
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

        [JsonIgnore]
        public string StatusIndicator
        {
            get { return Enabled ? "🟢" : "⚪"; }
        }

        [JsonIgnore]
        public string StatusColor
        {
            get { return Enabled ? "#10B981" : "#9CA3AF"; }
        }

        [JsonIgnore]
        public string TriggerBadge
        {
            get { return "[" + (Trigger != null ? (Trigger.RawType != "" ? Trigger.RawType : Trigger.Type.ToString().ToLowerInvariant()) : "unknown") + "]"; }
        }

        [JsonIgnore]
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

        [JsonIgnore]
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

        [JsonIgnore]
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
}
