using System;
using CarroDesk.Core;

namespace CarroDesk.Host.Services
{
    /// <summary>
    /// 把通知转发给外部委托（App 的托盘气泡）。供容器注册 INotificationService。
    /// </summary>
    public class DelegatedNotificationService : INotificationService
    {
        private readonly Action<string, string> _show;

        public DelegatedNotificationService(Action<string, string> show)
        {
            _show = show ?? throw new ArgumentNullException(nameof(show));
        }

        public void Show(string message, string title = "CarroDesk")
        {
            _show(message, title);
        }
    }
}