using System;
using System.ServiceProcess;
using System.Threading;

namespace CarroDesk.Modules.ServiceControl
{
    /// <summary>
    /// Windows 服务控制抽象：生产实现走 ServiceController（System.ServiceProcess），
    /// 单测注入假实现。实现方法允许抛出业务异常（服务不存在/拒绝访问/超时），
    /// 由能力层统一翻译为 CommandResult 错误码。
    /// </summary>
    public interface IServiceControlAdapter
    {
        /// <summary>查询服务状态；服务不存在返回 null。</summary>
        ServiceControllerStatus? GetStatus(string serviceName);

        /// <summary>启动服务并等待 Running；已运行/启动中则只等待。</summary>
        ServiceControllerStatus Start(string serviceName, TimeSpan timeout);

        /// <summary>停止服务并等待 Stopped；已停止/停止中则只等待。</summary>
        ServiceControllerStatus Stop(string serviceName, TimeSpan timeout);

        /// <summary>
        /// 可取消的启动。取消只保证"尽快停止等待并返回"，无法回滚已提交给 SCM 的启动命令
        /// ——服务可能仍会在后台完成启动，调用方需按"结果未知"处理。
        /// </summary>
        ServiceControllerStatus Start(string serviceName, TimeSpan timeout, CancellationToken token);

        /// <summary>可取消的停止，取消语义同 <see cref="Start(string,TimeSpan,CancellationToken)"/>。</summary>
        ServiceControllerStatus Stop(string serviceName, TimeSpan timeout, CancellationToken token);
    }
}
