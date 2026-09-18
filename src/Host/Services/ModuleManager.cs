using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using CarroDesk.Core;
using CarroDesk.Core.Models;

namespace CarroDesk.Host.Services
{
    public class ModuleManager : IDisposable
    {
        private readonly List<IModule> _modules = new List<IModule>();
        private readonly object _lock = new object();
        private ServiceContainer _services;

        /// <summary>供 App 注入的托盘重刷回调（App 自行做 150ms 防抖）。</summary>
        public Action RequestTrayRefresh { get; set; }

        /// <summary>供 App 注入的通知回调（通知节流由 App 负责）。</summary>
        public Action<string, string> ShowNotification { get; set; }

        public Dispatcher Dispatcher { get; set; }

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

        public void InitializeAll(ServiceContainer services)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            var logger = services.GetService<ILoggerService>();
            var configRegistry = services.GetService<IConfigRegistry>();

            if (configRegistry != null)
            {
                foreach (var module in Modules)
                {
                    try
                    {
                        module.RegisterConfig(configRegistry);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(module.Id, $"注册模块 '{module.Name}' 配置适配器失败", ex);
                    }
                }
            }

            foreach (var module in Modules.Where(m => m.DefaultEnabled))
            {
                var context = new ModuleContext(module.Id, services, Dispatcher, RequestTrayRefresh, ShowNotification);
                try
                {
                    module.Initialize(context);
                }
                catch (Exception ex)
                {
                    // ModuleBase.Initialize 已把 Status 置为 Faulted 并重抛，此处只记日志
                    logger?.LogError(module.Id, $"初始化模块 '{module.Name}' 失败", ex);
                }
            }
        }

        public void StartAll()
        {
            var logger = GetLogger();
            foreach (var module in Modules)
            {
                // 未完成初始化（例如 DefaultEnabled=false 维持在 Created）或初始化失败的模块禁止启动
                if (module.Status != ModuleStatus.Initialized) continue;
                try
                {
                    module.Start();
                }
                catch (Exception ex)
                {
                    // ModuleBase.Start 内部已把 Status 置为 Faulted，此处只记日志
                    logger?.LogError(module.Id, $"启动模块 '{module.Name}' 失败", ex);
                }
            }
        }

        public void StopAll()
        {
            var logger = GetLogger();
            foreach (var module in Modules.Reverse().ToList())
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
            var logger = GetLogger();
            foreach (var module in Modules)
            {
                if (module.Status == ModuleStatus.Faulted) continue;
                SafeInvoker.Run(module.Id, () => module.OnConfigReloaded(), (id, ex) => logger?.LogError(id, "重载模块配置失败", ex));
            }
        }

        public void OnLanguageChangedAll()
        {
            var logger = GetLogger();
            foreach (var module in Modules)
            {
                if (module.Status == ModuleStatus.Faulted) continue;
                SafeInvoker.Run(module.Id, () => module.OnLanguageChanged(), (id, ex) => logger?.LogError(id, "模块语言切换回调失败", ex));
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
            var logger = GetLogger();
            foreach (var module in Modules)
            {
                // 未初始化或已损坏的模块不显示在托盘菜单中
                if (module.Status != ModuleStatus.Initialized && module.Status != ModuleStatus.Running) continue;
                foreach (var item in GetItemsGuarded(module, logger))
                {
                    items.Add(item);
                }
            }
            return items;
        }

        private IEnumerable<TrayMenuItem> GetItemsGuarded(IModule module, ILoggerService logger)
        {
            try
            {
                var moduleItems = module.GetTrayMenuItems();
                return moduleItems ?? Enumerable.Empty<TrayMenuItem>();
            }
            catch (Exception ex)
            {
                logger?.LogError(module.Id, "获取托盘菜单项失败", ex);
                return Enumerable.Empty<TrayMenuItem>();
            }
        }

        private ILoggerService GetLogger()
        {
            return _services?.GetService<ILoggerService>();
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

        private sealed class ModuleContext : IModuleContext
        {
            private readonly ServiceContainer _container;
            private readonly Dispatcher _dispatcher;
            private readonly Action _requestTrayRefresh;
            private readonly Action<string, string> _showNotification;

            public ModuleContext(
                string moduleId,
                ServiceContainer container,
                Dispatcher dispatcher,
                Action requestTrayRefresh,
                Action<string, string> showNotification)
            {
                ModuleId = moduleId;
                _container = container;
                _dispatcher = dispatcher;
                _requestTrayRefresh = requestTrayRefresh;
                _showNotification = showNotification;
            }

            public string ModuleId { get; }
            public Dispatcher Dispatcher => _dispatcher;

            public T GetService<T>() where T : class
            {
                return _container?.GetService<T>();
            }

            public void RequestTrayRefresh()
            {
                _requestTrayRefresh?.Invoke();
            }

            public void ShowNotification(string message, string title = "CarroDesk")
            {
                _showNotification?.Invoke(message, title);
            }
        }
    }
}