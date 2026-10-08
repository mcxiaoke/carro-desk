using System;
using System.Collections.Generic;
using System.IO;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Host.Services;
using CarroDesk.Modules.TaskScheduler.Models;
using CarroDesk.Modules.TaskScheduler.Views;
using CarroDesk.Services;
using CarroDesk.Services.Localization;
using CarroDesk.Services.Tasks;
using CarroDesk.Models;

namespace CarroDesk.Modules.TaskScheduler
{
    public class TaskSchedulerModule : ModuleBase<TaskSchedulerConfig>
    {
        public override string Id => "TaskScheduler";
        public override string Name => Loc.T("Tray.Tasks", "自动化计划任务");
        public override string Description => Loc.T("Tasks.ModuleDesc", "支持 Cron/周期/定时/文件监听/会话切换等自动化脚本调度");

        public TaskSchedulerService Scheduler { get; private set; }

        public bool IsGlobalEnabled => Scheduler != null && Scheduler.IsGlobalEnabled;

        public TaskSchedulerModule()
        {
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
                    var cfgMgr = Context?.GetService<IConfigManager>();
                    var notif = Context?.GetService<INotificationService>();
                    var hotkeys = Context?.GetService<IHotkeyService>();
                    Scheduler = new TaskSchedulerService(idle, cfgMgr, notif, msg => Context?.ShowNotification(msg), hotkeys, Context?.Dispatcher);
                }
                Scheduler?.Start();
                Scheduler.StateChanged += (name, state, snap) => RequestRefreshSelf();
            }
            catch (Exception ex)
            {
                Context?.GetService<ILoggerService>()?.LogError(Id, "启动任务调度器失败", ex);
                throw;
            }
        }

        public override void Dispose()
        {
            // Scheduler 订阅了静态 SystemEvents.PowerModeChanged，Dispose 内会解绑
            Scheduler?.Dispose();
            base.Dispose();
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
            UpdateTrayHeader();
        }

        public override void OnLanguageChanged()
        {
            base.OnLanguageChanged();
            UpdateTrayHeader();
        }

        private TrayMenuItem _trayRoot;

        public string BuildTaskSchedulerHeader()
        {
            string baseTitle = Loc.T("Tray.TasksRoot", "自动化任务");
            string status = IsGlobalEnabled ? Loc.T("Tray.Running", "运行中") : Loc.T("Tray.Paused", "已暂停");
            return $"{baseTitle} ({status})";
        }

        private void UpdateTrayHeader()
        {
            if (_trayRoot == null) return;
            var d = Context?.Dispatcher;
            if (d != null && !d.CheckAccess())
            {
                d.BeginInvoke(new Action(UpdateTrayHeader));
                return;
            }
            _trayRoot.Header = BuildTaskSchedulerHeader();
        }

        public bool SetGlobalEnabled(bool enabled)
        {
            bool success = Scheduler?.SetGlobalEnabled(enabled) ?? false;
            // Scheduler 负责事务化持久化并在失败时回滚；模块只同步内存镜像。
            if (Config != null) Config.GlobalEnabled = Scheduler?.IsGlobalEnabled ?? Config.GlobalEnabled;
            UpdateTrayHeader();
            RequestRefreshSelf();
            return success;
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
                Header = BuildTaskSchedulerHeader()
            };
            _trayRoot = root;

            // 1. 总开关
            root.Children.Add(new TrayMenuItem
            {
                Id = "task_scheduler_toggle",
                Header = Loc.T("Tray.TasksEnabled", "启用任务调度"),
                IsChecked = IsGlobalEnabled,
                ClickAction = () =>
                {
                    bool enabled = !IsGlobalEnabled;
                    if (SetGlobalEnabled(enabled))
                        LogInfo(enabled ? "任务调度总开关已启用" : "任务调度总开关已禁用");
                }
            });

            root.Children.Add(TrayMenuItem.Separator());

            // 2. 主面板入口：任务管理...
            root.Children.Add(new TrayMenuItem
            {
                Id = "task_scheduler_manager",
                Header = Loc.T("Tray.TaskManager", "任务管理..."),
                ClickAction = () =>
                {
                    try
                    {
                        var win = new TaskManagerWindow(Scheduler, () => RequestRefreshSelf())
                        {
                            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen
                        };
                        win.ShowDialog();
                        RequestRefreshSelf();
                    }
                    catch { }
                }
            });

            root.Children.Add(TrayMenuItem.Separator());

            // 3. 运行中任务 (N) 动态分组
            var runningList = Scheduler != null ? Scheduler.GetRunning() : null;
            int runningCount = runningList != null ? runningList.Count : 0;
            var runningGroup = new TrayMenuItem
            {
                Id = "task_scheduler_running_group",
                Header = Loc.T("Tray.RunningTasks", "运行中任务 ({0})", runningCount)
            };
            if (runningCount == 0)
            {
                runningGroup.Children.Add(new TrayMenuItem
                {
                    Id = "task_scheduler_running_empty",
                    Header = Loc.T("Tray.NoRunningTasks", "暂无运行中的任务"),
                    IsEnabled = false
                });
            }
            else
            {
                foreach (var r in runningList)
                {
                    string rName = r.Name;
                    int rPid = r.Pid;
                    var item = new TrayMenuItem
                    {
                        Id = "task_scheduler_running_" + rName + "_" + rPid,
                        Header = $"⏹ {rName} (PID {rPid}) · 点击停止",
                        ClickAction = () =>
                        {
                            Scheduler?.TryStop(rName);
                            LogInfo($"通过托盘停止任务 '{rName}' (PID {rPid})");
                        }
                    };
                    runningGroup.Children.Add(item);
                }
            }
            root.Children.Add(runningGroup);

            // 4. 手动任务分组
            var manual = new TrayMenuItem
            {
                Id = "task_scheduler_manual",
                Header = Loc.T("Tray.ManualTasks", "手动任务")
            };
            var manualTasks = Scheduler != null && Scheduler.Tasks != null
                ? System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(Scheduler.Tasks, t => t.Enabled && (t.Trigger.Type == TaskTriggerType.Manual || t.Trigger.Type == TaskTriggerType.Hotkey)))
                : new List<TaskDefinition>();

            if (manualTasks.Count == 0)
            {
                manual.Children.Add(new TrayMenuItem
                {
                    Id = "task_scheduler_manual_empty",
                    Header = Loc.T("Tray.NoManualTasks", "暂无手动任务"),
                    IsEnabled = false
                });
            }
            else
            {
                foreach (var t in manualTasks)
                {
                    string name = t.Name;
                    string badge = t.TriggerBadgeText;
                    var mi = new TrayMenuItem
                    {
                        Id = "task_scheduler_manual_" + name,
                        Header = $"{name} [{badge}]"
                    };
                    if (t.Trigger != null && t.Trigger.Type == TaskTriggerType.Hotkey && !string.IsNullOrWhiteSpace(t.Trigger.Hotkey))
                    {
                        mi.InputGestureText = t.Trigger.Hotkey;
                    }
                    mi.ClickAction = () =>
                    {
                        bool ok = RunManual(name);
                        LogInfo($"用户手动触发任务 '{name}'，结果: {(ok ? "成功" : "失败")}");
                    };
                    manual.Children.Add(mi);
                }
            }
            root.Children.Add(manual);

            items.Add(root);
            return items;
        }

        private void RequestRefreshSelf()
        {
            try { Context?.RequestTrayRefresh(); } catch { }
        }

        private void NotifySelf(string msg)
        {
            try { Context?.ShowNotification(msg, "CarroDesk"); } catch { }
        }
    }
}
