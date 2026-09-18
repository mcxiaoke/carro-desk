using System;

namespace CarroDesk.Services
{
    /// <summary>
    /// 锁屏能力接口（活引用，非快照）。LockWindow/VerifyPinWindow 经由本接口消费，
    /// 避免直访 App.Controller 静态门面，同时消除对具体 LockController 的耦合。
    /// </summary>
    public interface ILockService
    {
        bool IsLocked { get; }
        TimeSpan GetBlockRemaining();
        PinAttemptResult TryUnlock(string pin, out string error);
    }
}