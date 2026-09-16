using System;

namespace CarroDesk.Core
{
    /// <summary>
    /// 纯物理闲时服务（不掺业务状态机）。唯一 1s Timer 在后台线程扇出 IdleTick。
    /// WarnBefore/阈值/暂停/白名单等业务判定由具体模块自行处理，不在此层。
    /// </summary>
    public interface IIdleService
    {
        TimeSpan RawIdle { get; }
        bool IsSystemBusyCached { get; }
        event Action<TimeSpan> IdleTick;
        event Action UserActiveDetected;
        event Action<bool> SystemBusyChanged;
    }
}