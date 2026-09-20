using System;
using CarroDesk.Core;
using CarroDesk.Host.Services;
using CarroDesk.Modules.AppAutoMute;
using CarroDesk.Modules.AudioSwitch;
using CarroDesk.Modules.Awake;
using CarroDesk.Modules.ClipboardHistory;
using CarroDesk.Modules.MonitorProfile;
using CarroDesk.Modules.ScreenLock;
using CarroDesk.Modules.TaskScheduler;

namespace CarroDesk.Host.Modules
{
    /// <summary>
    /// 业务模块装配清单（P2-12）：宿主对"有哪些模块"的全部认识收敛于此。
    /// App 不再逐个 new 模块，只调用本清单一次；模块向宿主暴露的服务出口
    /// （如 TaskScheduler 提供 ITaskSchedulerService）也统一在此注册。
    /// </summary>
    public static class ModuleRegistry
    {
        public static void RegisterStandardModules(ModuleManager modules, ServiceContainer services, Func<bool> isShuttingDown)
        {
            if (modules == null) throw new ArgumentNullException(nameof(modules));
            if (services == null) throw new ArgumentNullException(nameof(services));

            // 宿主级只读状态契约：供模块感知退出等状态，替代 App 直插具体类型
            services.AddSingleton<IHostStatusProvider>(new HostStatusProvider(isShuttingDown));

            var screenLock = new ScreenLockModule();
            modules.RegisterModule(screenLock);

            var taskScheduler = new TaskSchedulerModule();
            modules.RegisterModule(taskScheduler);
            // 任务调度器是模块向宿主暴露的跨模块服务出口（懒解析：Scheduler 在 OnStart 创建）
            services.AddSingleton<ITaskSchedulerService>(_ => taskScheduler.Scheduler);

            modules.RegisterModule(new AudioSwitchModule());
            modules.RegisterModule(new AppAutoMuteModule());
            modules.RegisterModule(new MonitorProfileModule());
            modules.RegisterModule(new AwakeModule());
            modules.RegisterModule(new ClipboardHistoryModule());
        }

        private sealed class HostStatusProvider : IHostStatusProvider
        {
            private readonly Func<bool> _isShuttingDown;
            public HostStatusProvider(Func<bool> isShuttingDown) => _isShuttingDown = isShuttingDown;
            public bool IsShuttingDown => _isShuttingDown?.Invoke() ?? false;
        }
    }
}