namespace CarroDesk.Services
{
    /// <summary>
    /// 锁屏窗口的只读配置视图（活引用，读取当前生效值，非一次性快照）。
    /// </summary>
    public interface ILockAppearance
    {
        bool ShowClock { get; }
        double OverlayOpacity { get; }
    }
}