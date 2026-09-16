using System;
using System.Collections.Generic;
using System.IO;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Host.Services;
using CarroDesk.Modules.TaskScheduler.Models;
using CarroDesk.Services;
using CarroDesk.Services.Localization;
using CarroDesk.Services.Tasks;

namespace CarroDesk.Modules.TaskScheduler
{
    public class TaskSchedulerModule : ModuleBase<TaskSchedulerConfig>
    {
        public static TaskSchedulerModule Instance { get; private set; }

        public override string Id => "TaskScheduler";
        public override string Name => Loc.T("Tray.Tasks", "自动化计划任务");
        public override string Description => "支持 Cron/周期/定时/文件监听/会话切换等自动化脚本调度";

        public TaskSchedulerService Scheduler { get; private set; }

        public bool IsGlobalEnabled => Scheduler != null && Scheduler.IsGlobalEnabled;

        public TaskSchedulerModule()
        {
            Instance = this;
        }

        protected override void OnStart()
        {
            try
            {
                Directory.CreateDirectory(ConfigService.ScriptsDirPath);
            }
            catch { }

            try
            {
                if (Scheduler == null)
                {
                    var idle = Context?.GetService<IIdleService>();
                    Scheduler = new TaskSchedulerService(idle);
                }
                Scheduler?.Start();
            }
            catch (Exception ex)
            {
                Context?.GetService<ILoggerService>()?.LogError(Id, "启动任务调度器失败", ex);
            }
        }

        protected override void OnStop()
        {
            try
            {
                Scheduler?.Stop();
            }
            catch (Exception ex)
            {
                Context?.GetService<ILoggerService>()?.LogError(Id, "停止任务调度器失败", ex);
            }
        }

        public override void OnConfigReloaded()
        {
            base.OnConfigReloaded();
            try
            {
                Scheduler?.Reload();
            }
            catch (Exception ex)
            {
                Context?.GetService<ILoggerService>()?.LogError(Id, "重载任务调度器失败", ex);
            }
        }

        public void SetGlobalEnabled(bool enabled)
        {
            Scheduler?.SetGlobalEnabled(enabled);
            if (Config != null)
            {
                Config.GlobalEnabled = enabled;
                var configMgr = Context?.GetService<IConfigManager>();
                configMgr?.SaveModuleConfig(Id, Config);
            }
        }

        public bool RunManual(string taskName)
        {
            return Scheduler != null && Scheduler.RunManual(taskName);
        }

        public bool Reload()
        {
            if (Scheduler == null) return false;
            var res = Scheduler.Reload();
            return res != null && (res.Errors == null || res.Errors.Count == 0);
        }

        public override IEnumerable<TrayMenuItem> GetTrayMenuItems()
        {
            var items = new List<TrayMenuItem>();
            var root = new TrayMenuItem
            {
                Id = "task_scheduler_root",
                Header = Loc.T("Tray.Tasks", "自动化计划任务")
            };

            root.Children.Add(new TrayMenuItem
            {
                Id = "task_scheduler_toggle",
                Header = Loc.T("Tray.TasksEnable", "启用任务调度"),
                IsChecked = IsGlobalEnabled,
                ClickAction = () => SetGlobalEnabled(!IsGlobalEnabled)
            });

            root.Children.Add(new TrayMenuItem
            {
                Id = "task_scheduler_reload",
                Header = Loc.T("Tray.ReloadTasks", "重载任务"),
                ClickAction = () => Reload()
            });

            items.Add(root);
            return items;
        }
    }
}
