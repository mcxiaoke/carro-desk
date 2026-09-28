using System;
using System.ServiceProcess;

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
    }
}
