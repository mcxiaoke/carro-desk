using System;
using System.Collections.Generic;
using CarroDesk.Core.Models;

namespace CarroDesk.Core
{
    public enum ModuleStatus
    {
        Created = 0,
        Initialized = 1,
        Running = 2,
        Stopped = 3,
        Disabled = 4,
        Faulted = 5
    }

    public interface IModule : IDisposable
    {
        string Id { get; }
        string Name { get; }
        string Description { get; }
        string Version { get; }
        int Order { get; }                    // 托盘排序 + 启动顺序，越小越靠前
        bool DefaultEnabled { get; }
        bool IsRunning { get; }
        ModuleStatus Status { get; }

        void Initialize(IModuleContext context);
        void Start();
        void Stop();
        void OnConfigReloaded();
        void OnLanguageChanged();
        void RegisterConfig(IConfigRegistry registry);

        IEnumerable<TrayMenuItem> GetTrayMenuItems();
    }
}