using System;
using System.ComponentModel;
using System.ServiceProcess;
using System.Threading;

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
            return Start(serviceName, timeout, CancellationToken.None);
        }

        public ServiceControllerStatus Stop(string serviceName, TimeSpan timeout)
        {
            return Stop(serviceName, timeout, CancellationToken.None);
        }

        public ServiceControllerStatus Start(string serviceName, TimeSpan timeout, CancellationToken token)
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
                return WaitForStatus(controller, ServiceControllerStatus.Running, timeout, token);
            }
        }

        public ServiceControllerStatus Stop(string serviceName, TimeSpan timeout, CancellationToken token)
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
                return WaitForStatus(controller, ServiceControllerStatus.Stopped, timeout, token);
            }
        }

        /// <summary>
        /// 等待服务到达目标状态。
        ///
        /// 不用 <c>ServiceController.WaitForStatus(status, timeout)</c>：它只有一个超时参数，
        /// 内核能力超时后我们仍要傻等到自己的 timeout（调用方早已拿到超时），
        /// 且中途无法响应取消。改为短周期轮询，让"外部超时"与"调用方取消"都能立刻生效。
        ///
        /// 轮询间隔 200ms：远小于人可感知阈值，同时把 SCM 查询压力压到可忽略。
        /// </summary>
        private static ServiceControllerStatus WaitForStatus(
            ServiceController controller, ServiceControllerStatus target, TimeSpan timeout, CancellationToken token)
        {
            var effective = timeout <= TimeSpan.Zero ? DefaultWait : timeout;
            var deadline = DateTime.UtcNow + effective;
            var waitFor = TimeSpan.FromMilliseconds(200);

            while (true)
            {
                // 取消优先于状态检查：调用方已经超时/放弃，再去读一次状态没有意义
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);

                controller.Refresh();
                if (controller.Status == target) return controller.Status;

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw new System.TimeoutException("timeout waiting for '" + controller.ServiceName + "' to reach " + target);

                // Sleep 期间保持可响应取消；无令牌时退化为普通等待
                if (token.CanBeCanceled) token.WaitHandle.WaitOne(waitFor < remaining ? waitFor : remaining);
                else Thread.Sleep(waitFor < remaining ? waitFor : remaining);
            }
        }
    }
}
