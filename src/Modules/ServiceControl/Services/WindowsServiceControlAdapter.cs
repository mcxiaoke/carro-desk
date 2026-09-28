using System;
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
            }
        }

        public ServiceControllerStatus Start(string serviceName, TimeSpan timeout)
        {
            using (var controller = new ServiceController(serviceName))
            {
                controller.Refresh();
                if (controller.Status != ServiceControllerStatus.Running
                    && controller.Status != ServiceControllerStatus.StartPending)
                {
                    controller.Start();
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
                if (controller.Status != ServiceControllerStatus.Stopped
                    && controller.Status != ServiceControllerStatus.StopPending)
                {
                    controller.Stop();
                }
                controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout <= TimeSpan.Zero ? DefaultWait : timeout);
                return controller.Status;
            }
        }
    }
}
