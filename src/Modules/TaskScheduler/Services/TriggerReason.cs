using System;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks
{
    /// <summary>
    /// 一次触发的结构化信息。
    /// ITrigger.Fired 仍传字符串 reason（不改事件签名、不破坏 10 个触发器实现），
    /// 但所有业务判定统一经 <see cref="TriggerReason.Parse"/> 解析，避免
    /// OnTriggerFired 的 StartsWith、ExecuteAsync 的 string.Equals、限流门的前缀匹配各写一套。
    /// </summary>
    public sealed class TriggerFireInfo
    {
        public string Raw { get; set; } = "";
        public TaskTriggerType Type { get; set; } = TaskTriggerType.Unknown;
        public bool IsUserDriven { get; set; }
        public bool IsCatchUp { get; set; }
        public bool IsStartup { get; set; }
        public bool IsDaily { get; set; }
    }

    public static class TriggerReason
    {
        public static bool StartsWith(string reason, string prefix)
        {
            return !string.IsNullOrEmpty(reason) &&
                   reason.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 人工类触发。注意 HotkeyTrigger 发出的是 "hotkey:&lt;组合键&gt;"，
        /// 必须用前缀而非全等匹配（附录 B：旧 ExecuteAsync 用 string.Equals 恒为 false）。
        /// </summary>
        public static bool IsUserDriven(string reason)
        {
            return StartsWith(reason, "manual")
                || StartsWith(reason, "restart")
                || StartsWith(reason, "hotkey");
        }

        public static bool IsCatchUp(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return false;
            return reason.IndexOf("catchup", StringComparison.OrdinalIgnoreCase) >= 0
                || StartsWith(reason, "startup:runAtStartup");
        }

        public static TriggerFireInfo Parse(string reason)
        {
            var info = new TriggerFireInfo
            {
                Raw = reason ?? "",
                IsUserDriven = IsUserDriven(reason),
                IsCatchUp = IsCatchUp(reason),
                IsStartup = StartsWith(reason, "startup"),
                IsDaily = StartsWith(reason, "daily")
            };

            if (StartsWith(reason, "startup")) info.Type = TaskTriggerType.Startup;
            else if (StartsWith(reason, "interval")) info.Type = TaskTriggerType.Interval;
            else if (StartsWith(reason, "daily")) info.Type = TaskTriggerType.Daily;
            else if (StartsWith(reason, "cron")) info.Type = TaskTriggerType.Cron;
            else if (StartsWith(reason, "sessionLock")) info.Type = TaskTriggerType.SessionLock;
            else if (StartsWith(reason, "sessionUnlock")) info.Type = TaskTriggerType.SessionUnlock;
            else if (StartsWith(reason, "idle")) info.Type = TaskTriggerType.Idle;
            else if (StartsWith(reason, "manual")) info.Type = TaskTriggerType.Manual;
            else if (StartsWith(reason, "hotkey")) info.Type = TaskTriggerType.Hotkey;
            else if (StartsWith(reason, "watch")) info.Type = TaskTriggerType.Watch;

            return info;
        }
    }
}
