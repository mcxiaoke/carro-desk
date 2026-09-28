using System.Collections.Generic;

namespace CarroDesk.Modules.ServiceControl
{
    /// <summary>
    /// 服务控制模块配置。AllowedServices 是「允许经能力通道启停的服务清单」，
    /// 同时作为 services.* 能力的参数枚举白名单（AllowedValues）。
    /// 真正的安全边界是服务对象 DACL（IPC 设计 §11.2 路径 A）：
    /// 即使配置被改写，未授权服务在内核对象权限处仍然拒绝。
    /// </summary>
    public sealed class ServiceControlConfig
    {
        public List<string> AllowedServices { get; set; } = new List<string>();
    }
}
