using System;

namespace CarroDesk.Core
{
    /// <summary>
    /// 全局热键服务。按 moduleId 分组，便于模块 Stop/故障时一键回收。
    /// 冲突时返回非空 error 字符串；Register 失败返回 0。
    /// </summary>
    public interface IHotkeyService : IDisposable
    {
        int Register(string moduleId, string hotkeyStr, Action callback, out string error);
        void Unregister(string moduleId, int id);
        void UnregisterAll(string moduleId);
    }
}