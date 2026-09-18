namespace CarroDesk.Modules.Awake.Models
{
    public enum AwakeMode
    {
        /// <summary>被动/关闭模式：遵循系统默认电源策略</summary>
        Passive = 0,

        /// <summary>无限期保持唤醒</summary>
        Indefinite = 1,

        /// <summary>定时保持唤醒（倒计时模式）</summary>
        Timed = 2,

        /// <summary>保持唤醒至指定时刻</summary>
        UntilTime = 3
    }
}
