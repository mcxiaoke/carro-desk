using System;

namespace CarroDesk.Core
{
    public interface IConfigManager
    {
        T GetModuleConfig<T>(string moduleId) where T : class, new();
        void SaveModuleConfig<T>(string moduleId, T config) where T : class;
        void Reload();
        event Action ConfigReloaded;
    }
}
