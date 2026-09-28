namespace CarroDesk.Core.Commands
{
    /// <summary>
    /// 能力风险分级（IPC 设计 §4.1）。决定分发器的校验强度与远程可达性（§9.5/§11.6）。
    /// </summary>
    public enum CommandRisk
    {
        ReadOnly = 0,     // 只读查询
        Low = 1,          // 低危动作（锁屏、切音频、暂停计时）
        TaskExec = 2,     // 触发既有任务
        Privileged = 3    // 需要管理员/SYSTEM 权限或高风险副作用的动作（§11）
    }
}
