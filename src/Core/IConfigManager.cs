using System;

namespace CarroDesk.Core
{
    public interface IConfigManager
    {
        T GetModuleConfig<T>(string moduleId) where T : class, new();
        bool SaveModuleConfig<T>(string moduleId, T config) where T : class;
        bool Reload();
        event Action ConfigReloaded;
    }
}
