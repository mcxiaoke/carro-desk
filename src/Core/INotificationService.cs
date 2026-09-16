namespace CarroDesk.Core
{
    /// <summary>
    /// 通知服务：经宿主统一节流/展示的托盘气泡提示。
    /// </summary>
    public interface INotificationService
    {
        void Show(string message, string title = "CarroDesk");
    }
}