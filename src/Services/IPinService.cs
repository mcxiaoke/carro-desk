using CarroDesk.Models;

namespace CarroDesk.Services
{
    /// <summary>
    /// PIN 校验能力（下沉 Host，规范 §3.5）。缺模块时仍可校验：宿主从 IConfigManager
    /// 惰性读取当前 salt/hash，不依赖 ScreenLock 模块存活。
    /// </summary>
    public interface IPinService
    {
        bool IsConfigured { get; }
        string Salt { get; }
        string Hash { get; }
        bool Verify(string pin);
        void SetNewPin(string pin);
        void SetFromConfig(string salt, string hash);
    }
}