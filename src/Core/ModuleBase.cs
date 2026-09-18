using System;
using System.Collections.Generic;
using CarroDesk.Core.Models;

namespace CarroDesk.Core
{
    public abstract class ModuleBase<TConfig> : IModule where TConfig : class, new()
    {
        public abstract string Id { get; }
        public abstract string Name { get; }
        public virtual string Description => string.Empty;
        public virtual string Version => "1.0.0";
        public virtual int Order => 0;
        public virtual bool DefaultEnabled => true;

        public bool IsRunning { get; private set; }
        public ModuleStatus Status { get; protected set; } = ModuleStatus.Created;

        protected IModuleContext Context { get; private set; }
        public TConfig Config { get; private set; }

        public virtual void Initialize(IModuleContext context)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            try
            {
                var configMgr = Context.GetService<IConfigManager>();
                Config = configMgr != null ? configMgr.GetModuleConfig<TConfig>(Id) : new TConfig();
                Status = ModuleStatus.Initialized;
            }
            catch
            {
                Status = ModuleStatus.Faulted;
                throw;
            }
        }

        public void Start()
        {
            if (Status == ModuleStatus.Faulted)
            {
                // 初始化失败的模块禁止启动
                return;
            }
            if (IsRunning) return;
            try
            {
                OnStart();
                IsRunning = true;
                Status = ModuleStatus.Running;
            }
            catch (Exception ex)
            {
                Status = ModuleStatus.Faulted;
                Context?.GetService<ILoggerService>()?.LogError(Id, "启动模块时发生异常", ex);
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
                Context?.GetService<ILoggerService>()?.LogError(Id, "停止模块时发生异常", ex);
            }
            finally
            {
                IsRunning = false;
                Status = ModuleStatus.Stopped;
            }
        }

        public virtual void OnConfigReloaded()
        {
            var configMgr = Context?.GetService<IConfigManager>();
            if (configMgr != null)
            {
                Config = configMgr.GetModuleConfig<TConfig>(Id);
            }
        }

        public virtual void OnLanguageChanged()
        {
        }

        public virtual void RegisterConfig(IConfigRegistry registry)
        {
        }

        public virtual IEnumerable<TrayMenuItem> GetTrayMenuItems()
        {
            return System.Linq.Enumerable.Empty<TrayMenuItem>();
        }

        protected abstract void OnStart();
        protected abstract void OnStop();

        public virtual void Dispose()
        {
            Stop();
        }
    }
}