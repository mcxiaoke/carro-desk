using System;
using System.ComponentModel;
using System.ServiceProcess;

namespace CarroDesk.Modules.ServiceControl
{
    /// <summary>
    /// ServiceController 生产实现（IPC 设计 §11.2 路径 A）：进程全程普通权限，
    /// 启停授权来自服务对象 DACL 的一次性授予（docs/remote-admin/grant-service-control.ps1）。
    /// </summary>
    public sealed class WindowsServiceControlAdapter : IServiceControlAdapter
    {
        private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(15);

        public ServiceControllerStatus? GetStatus(string serviceName)
        {
            using (var controller = new ServiceController(serviceName))
            {
                try
                {
                    controller.Refresh();
                    return controller.Status;
                }
                catch (InvalidOperationException)
                {
                    return null; // 服务不存在
                }
                catch (Win32Exception)
                {
                    // 无权限访问 SCM（错误码 5）或服务已被删除：返回未知，避免单个服务拖垮整个列表
                    return null;
                }
            }
        }

        public ServiceControllerStatus Start(string serviceName, TimeSpan timeout)
        {
            using (var controller = new ServiceController(serviceName))
            {
                controller.Refresh();
                switch (controller.Status)
                {
                    case ServiceControllerStatus.Running:
                    case ServiceControllerStatus.StartPending:
                        break; // 已运行 / 正在启动：无需再调 Start
                    case ServiceControllerStatus.StopPending:
                        // 过渡态：对正在停止的服务调 Start 会抛 InvalidOperationException，
                        // 这里给出可理解的原因，避免被上层泛化为"服务不可用"。
                        throw new InvalidOperationException("服务正在停止中，请稍后重试 (service is stopping)");
                    default:
                        controller.Start();
                        break;
                }
                controller.WaitForStatus(ServiceControllerStatus.Running, timeout <= TimeSpan.Zero ? DefaultWait : timeout);
                return controller.Status;
            }
        }

        public ServiceControllerStatus Stop(string serviceName, TimeSpan timeout)
        {
            using (var controller = new ServiceController(serviceName))
            {
                controller.Refresh();
                switch (controller.Status)
                {
                    case ServiceControllerStatus.Stopped:
                    case ServiceControllerStatus.StopPending:
                        break; // 已停止 / 正在停止：无需再调 Stop
                    default:
                        controller.Stop();
                        break;
                }
                controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout <= TimeSpan.Zero ? DefaultWait : timeout);
                return controller.Status;
            }
        }
    }
}
