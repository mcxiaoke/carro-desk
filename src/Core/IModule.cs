using System;
using System.Collections.Generic;
using CarroDesk.Core.Models;

namespace CarroDesk.Core
{
    public interface IModule : IDisposable
    {
        string Id { get; }
        string Name { get; }
        string Description { get; }
        bool DefaultEnabled { get; }
        bool IsRunning { get; }

        void Initialize(IServiceProvider services);
        void Start();
        void Stop();
        void OnConfigReloaded();

        IEnumerable<TrayMenuItem> GetTrayMenuItems();
    }
}
