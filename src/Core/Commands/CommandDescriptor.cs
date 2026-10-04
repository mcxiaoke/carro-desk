using System;
using System.Collections.Generic;
using System.Threading;

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

        /// <summary>
        /// 按请求动态判定是否需要口令（§9.5 按服务 PIN 策略）。
        /// 在参数规整之后调用（可见规整后的 Params）；为 null 时退回 <see cref="RequiresPin"/>。
        /// 判定为 true 时走 PinGuard 挑战，失败限流与审计照常生效。
        /// </summary>
        public Func<CommandRequest, bool> RequiresPinFor { get; set; }

        /// <summary>执行超时（毫秒）。0 取内核默认（5000ms）；任务类能力建议 30000（§4.3 第 7 步）。</summary>
        public int TimeoutMs { get; set; }

        public IReadOnlyList<CommandParam> Params { get; set; }

        /// <summary>
        /// 执行体。在线程池上被调用（内核只做超时兜底），需要 UI/COM 的能力必须在实现内部
        /// 自行经 Context.Dispatcher 封送（沿用 ModuleBase 既有跨线程封装）。
        ///
        /// 内核超时只是"停止等待"，无法终止本委托：忽略超时的能力请改用
        /// <see cref="CancellableHandler"/>，否则调用方已收到 -32004，副作用却仍在后台生效。
        /// </summary>
        public Func<CommandRequest, CommandResult> Handler { get; set; }

        /// <summary>
        /// 可取消的执行体。设置后优先于 <see cref="Handler"/>：超时时内核先
        /// <c>CancellationTokenSource.Cancel()</c> 再返回 -32004，实现应在耗时点检查该令牌
        /// 并尽快退出，避免"用户看到超时、后台仍在继续"。
        ///
        /// 令牌只覆盖 handler 执行阶段，它<strong>不是</strong>对已发出外部操作的中断保证
        /// （如已提交给 SCM 的启动命令无法回滚）；实现只需停止后续步骤并尽快返回。
        /// </summary>
        public Func<CommandRequest, CancellationToken, CommandResult> CancellableHandler { get; set; }
    }
}
