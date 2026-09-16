using System;
using System.Collections.Generic;
using System.Linq;
using CarroDesk.Core;
using CarroDesk.Core.Models;

namespace CarroDesk.Host.Services
{
    public class ModuleManager : IDisposable
    {
        private readonly List<IModule> _modules = new List<IModule>();
        private readonly object _lock = new object();
        private IServiceProvider _services;

        public IReadOnlyList<IModule> Modules
        {
            get
            {
                lock (_lock)
                {
                    return _modules.ToList();
                }
            }
        }

        public void RegisterModule(IModule module)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            lock (_lock)
            {
                if (_modules.Any(m => m.Id.Equals(module.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"模块 ID '{module.Id}' 已存在。");
                }
                _modules.Add(module);
            }
        }

        public void InitializeAll(IServiceProvider services)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            var logger = services.GetService<ILoggerService>();

            foreach (var module in Modules)
            {
                try
                {
                    module.Initialize(services);
                }
                catch (Exception ex)
                {
                    logger?.LogError(module.Id, $"初始化模块 '{module.Name}' 失败", ex);
                }
            }
        }

        public void StartAll()
        {
            var logger = _services?.GetService<ILoggerService>();
            foreach (var module in Modules)
            {
                try
                {
                    module.Start();
                }
                catch (Exception ex)
                {
                    logger?.LogError(module.Id, $"启动模块 '{module.Name}' 失败", ex);
                }
            }
        }

        public void StopAll()
        {
            var logger = _services?.GetService<ILoggerService>();
            // 倒序停止
            var list = Modules.Reverse().ToList();
            foreach (var module in list)
            {
                try
                {
                    module.Stop();
                }
                catch (Exception ex)
                {
                    logger?.LogError(module.Id, $"停止模块 '{module.Name}' 失败", ex);
                }
            }
        }

        public void ReloadAll()
        {
            var logger = _services?.GetService<ILoggerService>();
            foreach (var module in Modules)
            {
                try
                {
                    module.OnConfigReloaded();
                }
                catch (Exception ex)
                {
                    logger?.LogError(module.Id, $"重载模块 '{module.Name}' 配置失败", ex);
                }
            }
        }

        public IModule GetModule(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock)
            {
                return _modules.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            }
        }

        public T GetModule<T>() where T : class, IModule
        {
            lock (_lock)
            {
                return _modules.OfType<T>().FirstOrDefault();
            }
        }

        public IEnumerable<TrayMenuItem> GetAllTrayMenuItems()
        {
            var items = new List<TrayMenuItem>();
            foreach (var module in Modules)
            {
                try
                {
                    var moduleItems = module.GetTrayMenuItems();
                    if (moduleItems != null)
                    {
                        items.AddRange(moduleItems);
                    }
                }
                catch (Exception ex)
                {
                    _services?.GetService<ILoggerService>()?.LogError(module.Id, "获取托盘菜单项失败", ex);
                }
            }
            return items;
        }

        public void Dispose()
        {
            StopAll();
            lock (_lock)
            {
                foreach (var module in _modules)
                {
                    try { module.Dispose(); } catch { }
                }
                _modules.Clear();
            }
        }
    }
}
