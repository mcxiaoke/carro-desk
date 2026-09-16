using System.Windows.Threading;

namespace CarroDesk.Core
{
    /// <summary>
    /// 模块运行上下文（薄上下文）。模块经它获取服务、请求托盘重刷、发通知。
    /// 只读消费，禁止向容器注册任何东西。
    /// </summary>
    public interface IModuleContext
    {
        string ModuleId { get; }
        Dispatcher Dispatcher { get; }
        T GetService<T>() where T : class;
        void RequestTrayRefresh();
        void ShowNotification(string message, string title = "CarroDesk");
    }
}