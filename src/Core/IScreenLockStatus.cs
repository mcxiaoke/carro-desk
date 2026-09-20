namespace CarroDesk.Core
{
    /// <summary>
    /// 屏锁只读状态契约。宿主经它读取锁屏相关状态（如闲时分钟数），
    /// 避免宿主 import 模块私有 Model（P2-12：App 不再消费 ScreenLock.Models.ScreenLockConfig）。
    /// </summary>
    public interface IScreenLockStatus
    {
        int IdleMinutes { get; }
    }
}