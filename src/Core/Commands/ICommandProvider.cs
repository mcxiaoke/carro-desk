using System.Collections.Generic;

namespace CarroDesk.Core.Commands
{
    /// <summary>
    /// 模块可选能力出口。与 IModule.GetTrayMenuItems() 同构：pull 模式，
    /// 宿主管收集与异常隔离（IPC 设计 §4.1）；模块禁止向容器注册任何东西。
    /// </summary>
    public interface ICommandProvider
    {
        IEnumerable<CommandDescriptor> GetCommands();
    }
}
