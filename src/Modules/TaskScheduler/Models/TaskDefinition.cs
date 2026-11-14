using System;
using System.Collections.Generic;
using CarroDesk.Services;
using CarroDesk.Services.Localization;
using CarroDesk.Services.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
        Watch,
        Unknown
    }

    public class TaskTrigger
    {
        public TaskTriggerType Type { get; set; } = TaskTriggerType.Unknown;
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
        public Dictionary<string, string> Env { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public class TaskOptions
    {
        public bool Hidden { get; set; } = true;
        public int TimeoutSec { get; set; } = 0;
        public bool AllowConcurrent { get; set; } = false;
        public int Retry { get; set; } = 0;
        public string WorkDir { get; set; } = "";
        public bool NotifyOnFailure { get; set; } = true;
        public string Encoding { get; set; } = "";
        public bool RunAtStartup { get; set; } = false;
        public bool CatchUpMissed { get; set; } = false;

        /// <summary>每天最多触发次数（0=不限，1..1000）。本地自然日 00:00 归零。见 TASK-FIRE-LIMITER-DESIGN-20261114.md。</summary>
        public int MaxRunsPerDay { get; set; } = 0;

        /// <summary>两次触发最短间隔秒数（0=不限，0..86400）。支持 "30m" / "1h30m"（由 GetInt 解析）。</summary>
        public int MinIntervalSec { get; set; } = 0;

        /// <summary>限流作用域："auto"（人工类触发绕过，默认）| "all"（全部触发受限）。</summary>
        public string LimitScope { get; set; } = "auto";

        /// <summary>服务层据此决定人工类触发是否也要经过限流门。</summary>
        [JsonIgnore]
        public bool LimitAppliesToUserDriven
        {
            get { return string.Equals(LimitScope, "all", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// 运行模式："wait"（默认，启动后等待退出，现状语义）| "detach"（启动即返回，进程后台常驻）。
        /// 用字符串而非枚举：tasks.json 是可手改文件，保持与既有小写键风格一致且无转换器依赖。
        /// </summary>
        public string Mode { get; set; } = "wait";

        /// <summary>宿主退出时是否连带终止该任务进程树（kill-on-close 作业对象）。常驻任务若需跨宿主存活须显式设 false。</summary>
        public bool KillWithHost { get; set; } = true;

        /// <summary>跨宿主重启的单实例互斥（任务级命名 mutex）。仅 detach 模式可用（校验拒绝其它组合）。</summary>
        public bool SingleInstance { get; set; } = false;

        /// <summary>
        /// 守护重启策略（仅 detach）： "none"（默认，意外退出只记录/通知）| "on-failure"（意外退出自动重启）。
        /// 参考 systemd Restart=on-failure / pm2 / Windows 任务计划的"失败时重启"。
        /// wait 模式的等价物是 retry，二者互斥（校验保证）。
        /// </summary>
        public string Restart { get; set; } = "none";

        /// <summary>重启延迟秒数（1..3600，默认 5）。固定间隔，与任务计划程序/systemd 默认行为一致。</summary>
        public int RestartDelaySec { get; set; } = 5;

        /// <summary>连续异常失败预算（1..100，默认 3）：预算内自动重启，耗尽即标记失败、停止自动重启。</summary>
        public int RestartLimit { get; set; } = 3;

        /// <summary>
        /// 稳定存活阈值秒（0..86400，默认 60）：实例存活达此时长后的退出重置连续失败计数
        /// （参考 pm2 min_uptime / supervisord startsecs，防止长稳运行后的偶发崩溃被历史失败连坐）。
        /// 0 表示计数永不自动复位。
        /// </summary>
        public int StableUptimeSec { get; set; } = 60;

        /// <summary>
        /// options 中未被本类识别的扩展字段（M11）：读取时原样保留，保存时原样回写，
        /// 避免 GUI 保存一次就把用户手写的自定义字段静默删除。
        /// 不参与 JsonConvert 直接序列化，只由 TaskConfigService 合并进 tasks.json。
        /// </summary>
        [JsonIgnore]
        public JObject ExtraOptions { get; set; }

        [JsonIgnore]
        public bool IsDetach
        {
            get { return string.Equals(Mode, "detach", StringComparison.OrdinalIgnoreCase); }
        }

        [JsonIgnore]
        public bool RestartOnFailure
        {
            get { return string.Equals(Restart, "on-failure", StringComparison.OrdinalIgnoreCase); }
        }
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

        public TaskDefinition Clone()
        {
            // ExtraOptions 是 [JsonIgnore] 的运行时载体，JsonConvert 往返会丢失，需单独搬运。
            var extras = Options?.ExtraOptions;
            var json = JsonConvert.SerializeObject(this);
            var clone = JsonConvert.DeserializeObject<TaskDefinition>(json) ?? new TaskDefinition();
            if (extras != null)
            {
                if (clone.Options == null) clone.Options = new TaskOptions();
                clone.Options.ExtraOptions = (JObject)extras.DeepClone();
            }
            return clone;
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
            if (Trigger.Type == TaskTriggerType.Unknown) return "trigger.type missing or unknown";
            if (Action == null || string.IsNullOrWhiteSpace(Action.File)) return "action.file required";
            if (Trigger.Type == TaskTriggerType.Startup && (Trigger.DelaySec < 0 || Trigger.DelaySec > 365 * 24 * 60 * 60))
                return "startup delaySec must be between 0 and 31536000";
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
            if (Options != null && (Options.MaxRunsPerDay < 0 || Options.MaxRunsPerDay > 1000))
                return "options.maxRunsPerDay must be 0..1000";
            if (Options != null && (Options.MinIntervalSec < 0 || Options.MinIntervalSec > 86400))
                return "options.minIntervalSec must be 0..86400";
            if (Options != null && !string.IsNullOrWhiteSpace(Options.LimitScope))
            {
                string scope = Options.LimitScope.Trim().ToLowerInvariant();
                if (scope != "auto" && scope != "all") return "options.limitScope invalid (auto|all)";
            }
            if (Options != null && !string.IsNullOrWhiteSpace(Options.Encoding))
            {
                // 与 TaskRunner 的消费集合保持一致（未知值会静默按 UTF-8 处理，配置期就拒绝更友好）
                string e = Options.Encoding.Trim().ToLowerInvariant();
                if (e != "utf8" && e != "utf-8" && e != "gbk" && e != "gb2312" && e != "cp936"
                    && e != "oem" && e != "default" && e != "ascii" && e != "unicode" && e != "utf16")
                    return "options.encoding invalid (utf8|gbk|oem|ascii|unicode)";
            }
            if (Options != null && !string.IsNullOrWhiteSpace(Options.Mode))
            {
                string m = Options.Mode.Trim().ToLowerInvariant();
                if (m != "wait" && m != "detach") return "options.mode invalid (wait|detach)";
            }
            if (Options != null && Options.IsDetach)
            {
                // 常驻语义与"等待退出"才有的机制互斥，宁可配置期报错也不静默失效
                if (Options.TimeoutSec > 0) return "detach mode conflicts with timeoutSec>0 (detached process is never timed out)";
                if (Options.Retry > 0) return "detach mode conflicts with retry>0 (no exit code to retry on)";
                if (Options.AllowConcurrent) return "detach mode conflicts with allowConcurrent=true (multiple daemon instances are almost always a mistake)";
            }
            if (Options != null && Options.SingleInstance && !Options.IsDetach)
                return "options.singleInstance requires mode=detach (wait mode is deduped in-host already)";
            if (Options != null && !string.IsNullOrWhiteSpace(Options.Restart))
            {
                string r = Options.Restart.Trim().ToLowerInvariant();
                if (r != "none" && r != "on-failure") return "options.restart invalid (none|on-failure)";
            }
            if (Options != null && Options.RestartOnFailure)
            {
                if (!Options.IsDetach) return "options.restart=on-failure requires mode=detach (wait mode uses retry instead)";
                if (Options.RestartDelaySec < 1 || Options.RestartDelaySec > 3600) return "restartDelaySec must be 1..3600";
                if (Options.RestartLimit < 1 || Options.RestartLimit > 100) return "restartLimit must be 1..100";
                if (Options.StableUptimeSec < 0 || Options.StableUptimeSec > 86400) return "stableUptimeSec must be 0..86400";
            }
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
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(Action.File);
                    if (string.IsNullOrEmpty(dir)) return "";   // 裸文件名：由 TaskRunner 解析脚本目录后兜底
                    if (System.IO.Path.IsPathRooted(dir)) return dir;

                    // 相对路径必须解析为绝对路径（M15）：否则工作目录取决于宿主当前目录，
                    // 宿主以不同方式启动时行为不一致。脚本按 scripts/ 解析（与 ScriptResolver 一致），
                    // 其它程序按数据目录解析。
                    string ext = System.IO.Path.GetExtension(Action.File);
                    string baseDir = ScriptResolver.IsScriptExtension(ext)
                        ? ConfigService.ScriptsDirPath
                        : ConfigService.DirPath;
                    string absolute = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, dir));
                    if (System.IO.Directory.Exists(absolute)) return absolute;

                    TaskLogger.Warn("system", "workDir not found, fallback to " + baseDir + ": " + absolute);
                    return baseDir;
                }
                catch { }
            }
            return "";
        }
    }
}
