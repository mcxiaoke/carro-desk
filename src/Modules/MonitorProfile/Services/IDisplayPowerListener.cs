using System;

namespace CarroDesk.Modules.MonitorProfile.Services
{
    /// <summary>
    /// 显示器电源状态监听接口。
    /// 提供当前显示器是否亮屏/开启的状态，以及亮屏/熄屏状态变更事件。
    /// </summary>
    public interface IDisplayPowerListener : IDisposable
    {
        /// <summary>
        /// 当前控制台显示器是否处于点亮开启状态（true=亮屏，false=关闭/变暗/休眠）。
        /// </summary>
        bool IsDisplayOn { get; }

        /// <summary>
        /// 当显示器亮屏或熄屏状态发生变更时触发（参数 true=开启，false=关闭）。
        /// </summary>
        event Action<bool> DisplayPowerChanged;

        /// <summary>
        /// 启动监听。
        /// </summary>
        void Start();

        /// <summary>
        /// 停止监听。
        /// </summary>
        void Stop();
    }
}
