using System;
using System.Collections.Generic;
using CarroDesk.Core.Models;

namespace CarroDesk.Core
{
    /// <summary>
    /// 音频能力服务契约：默认端点切换 + 进程静音 + 设备枚举。
    /// DevicesChanged：WM_DEVICECHANGE 桥接，音频菜单刷新用。
    /// </summary>
    public interface IAudioService : IDisposable
    {
        List<AudioDeviceItem> GetPlaybackDevices();
        AudioDeviceItem GetDefaultPlaybackDevice();
        AudioDeviceItem FindDeviceByPattern(string pattern);
        bool SetDefaultPlaybackDevice(string deviceId);
        bool SetProcessMute(string processName, bool mute);
        void UnmuteProcesses(IEnumerable<string> processNames);
        List<string> GetActiveAudioProcesses();
        event Action DevicesChanged;
    }
}