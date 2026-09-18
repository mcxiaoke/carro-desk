using System;
using System.Collections.Generic;

namespace CarroDesk.Modules.Awake.Models
{
    public class AwakeConfig
    {
        /// <summary>模块总开关</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>保持唤醒模式：Passive, Indefinite, Timed, UntilTime</summary>
        public AwakeMode Mode { get; set; } = AwakeMode.Passive;

        /// <summary>是否保持显示器常亮（true: 系统+屏幕；false: 仅系统不休眠，允许关屏）</summary>
        public bool KeepDisplayOn { get; set; } = true;

        /// <summary>定时唤醒默认时长（分钟）</summary>
        public int DefaultDurationMinutes { get; set; } = 30;

        /// <summary>全局快速切换热键</summary>
        public string Hotkey { get; set; } = "Win+Shift+W";

        /// <summary>使用电池供电时是否自动暂停保持唤醒（保护笔记本电池）</summary>
        public bool DisableOnBattery { get; set; } = true;

        /// <summary>电池电量低于指定百分比时自动暂停保持唤醒</summary>
        public int BatteryThreshold { get; set; } = 20;

        /// <summary>常用定时预设（分钟）</summary>
        public List<int> CustomPresets { get; set; } = new List<int> { 15, 30, 60, 120, 240 };

        /// <summary>检测到以下进程运行时自动保持唤醒</summary>
        public List<string> AutoAwakeProcesses { get; set; } = new List<string>();

        public static AwakeConfig CreateDefault()
        {
            return new AwakeConfig();
        }
    }
}
