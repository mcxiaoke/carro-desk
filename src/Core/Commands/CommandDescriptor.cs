using System;
using System.Collections.Generic;

namespace CarroDesk.Core.Commands
{
    /// <summary>
    /// 能力描述符：全局唯一的白名单条目（IPC 设计 §4.1）。
    /// 模块经 <see cref="ICommandProvider"/> pull 暴露（与 GetTrayMenuItems 同构）；
    /// <see cref="ModuleId"/> 由注册表填充，模块无需自填。
    /// </summary>
    public sealed class CommandDescriptor
    {
        /// <summary>全局唯一，形如 "screenlock.lock"（全小写点分两段）。</summary>
        public string Name { get; set; }

        public string ModuleId { get; set; }

        public string Summary { get; set; }

        public CommandRisk Risk { get; set; }

        public bool RequiresPin { get; set; }

        /// <summary>执行超时（毫秒）。0 取内核默认（5000ms）；任务类能力建议 30000（§4.3 第 7 步）。</summary>
        public int TimeoutMs { get; set; }

        public IReadOnlyList<CommandParam> Params { get; set; }

        /// <summary>
        /// 执行体。在线程池上被调用（内核只做超时兜底），需要 UI/COM 的能力必须在实现内部
        /// 自行经 Context.Dispatcher 封送（沿用 ModuleBase 既有跨线程封装）。
        /// </summary>
        public Func<CommandRequest, CommandResult> Handler { get; set; }
    }
}
