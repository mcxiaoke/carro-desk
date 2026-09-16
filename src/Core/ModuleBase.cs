using System;
using System.Collections.Generic;
using System.Linq;
using CarroDesk.Core.Models;
using CarroDesk.Host.Services;

namespace CarroDesk.Core
{
    public abstract class ModuleBase<TConfig> : IModule where TConfig : class, new()
    {
        public abstract string Id { get; }
        public abstract string Name { get; }
        public virtual string Description => string.Empty;
        public virtual bool DefaultEnabled => true;
        public bool IsRunning { get; private set; }

        protected IServiceProvider Services { get; private set; }
        public TConfig Config { get; private set; }

        public virtual void Initialize(IServiceProvider services)
        {
            Services = services ?? throw new ArgumentNullException(nameof(services));
            var configMgr = services.GetService<IConfigManager>();
            Config = configMgr != null ? configMgr.GetModuleConfig<TConfig>(Id) : new TConfig();
        }

        public void Start()
        {
            if (IsRunning) return;
            try
            {
                OnStart();
                IsRunning = true;
            }
            catch (Exception ex)
            {
                Services?.GetService<ILoggerService>()?.LogError(Id, "启动模块时发生异常", ex);
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            try
            {
                OnStop();
            }
            catch (Exception ex)
            {
                Services?.GetService<ILoggerService>()?.LogError(Id, "停止模块时发生异常", ex);
            }
            finally
            {
                IsRunning = false;
            }
        }

        public virtual void OnConfigReloaded()
        {
            var configMgr = Services?.GetService<IConfigManager>();
            if (configMgr != null)
            {
                Config = configMgr.GetModuleConfig<TConfig>(Id);
            }
        }

        public virtual IEnumerable<TrayMenuItem> GetTrayMenuItems()
        {
            return Enumerable.Empty<TrayMenuItem>();
        }

        protected abstract void OnStart();
        protected abstract void OnStop();

        public virtual void Dispose()
        {
            Stop();
        }
    }
}
