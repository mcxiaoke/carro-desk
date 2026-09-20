namespace CarroDesk.Core
{
    /// <summary>
    /// 宿主级状态只读契约。让模块感知退出等宿主状态时不再直引宿主类型，
    /// 保持"宿主零感知业务、业务仅经抽象依赖宿主"的依赖方向。
    /// </summary>
    public interface IHostStatusProvider
    {
        bool IsShuttingDown { get; }
    }
}