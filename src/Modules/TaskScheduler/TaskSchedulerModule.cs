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
using CarroDesk.Models;

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

        private readonly ConfigService _configService;

        public TaskSchedulerModule(ConfigService configService = null)
        {
            Instance = this;
            _configService = configService;
        }

        public override void RegisterConfig(IConfigRegistry registry)
        {
            registry?.Register<TaskSchedulerConfig>(Id,
                () =>
                {
                    var cfg = _configService?.Current;
                    return new TaskSchedulerConfig
                    {
                        GlobalEnabled = cfg?.TasksEnabled ?? true,
                        TasksFile = ConfigService.TaskFilePath
                    };
                },
                c =>
                {
                    if (c == null) return;
                    if (_configService?.Current != null)
                    {
                        _configService.Current.TasksEnabled = c.GlobalEnabled;
                        _configService.Save();
                    }
                    Scheduler?.SetGlobalEnabled(c.GlobalEnabled);
                    UpdateTrayHeader();
                });
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

        public void SetGlobalEnabled(bool enabled)
        {
            Scheduler?.SetGlobalEnabled(enabled);
            if (Config != null && Config.GlobalEnabled != enabled)
            {
                Config.GlobalEnabled = enabled;
                var configMgr = Context?.GetService<IConfigManager>();
                configMgr?.SaveModuleConfig(Id, Config);
            }
            UpdateTrayHeader();
            RequestRefreshSelf();
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
            // 任务子菜单全量并入本模块（规范 §4.1）
            var items = new List<TrayMenuItem>();
            var root = new TrayMenuItem
            {
                Id = "task_scheduler_root",
                Header = BuildTaskSchedulerHeader()
            };
            _trayRoot = root;

            root.Children.Add(new TrayMenuItem
            {
                Id = "task_scheduler_toggle",
                Header = Loc.T("Tray.TasksEnabled", "启用任务调度"),
                IsChecked = IsGlobalEnabled,
                ClickAction = () =>
                {
                    bool enabled = !IsGlobalEnabled;
                    SetGlobalEnabled(enabled);
                    NotifySelf(enabled ? "任务调度已启用" : "任务调度已禁用");
                }
            });

            root.Children.Add(TrayMenuItem.Separator());

            // 手动运行：动态枚举 manual 任务
            var manual = new TrayMenuItem
            {
                Id = "task_scheduler_manual",
                Header = Loc.T("Tray.ManualRun", "手动运行")
            };
            var manuals = Scheduler != null ? Scheduler.GetManualTasks() : null;
            if (manuals == null || manuals.Count == 0)
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
                foreach (var t in manuals)
                {
                    string name = t.Name;
                    var mi = new TrayMenuItem
                    {
                        Id = "task_scheduler_manual_" + name,
                        Header = name
                    };
                    if (t.Trigger != null && t.Trigger.Type == TaskTriggerType.Hotkey && !string.IsNullOrWhiteSpace(t.Trigger.Hotkey))
                    {
                        mi.InputGestureText = t.Trigger.Hotkey;
                    }
                    mi.ClickAction = () =>
                    {
                        bool ok = RunManual(name);
                        NotifySelf(ok ? Loc.T("Tray.TaskTriggered", name) : Loc.T("Tray.TaskTriggerFailed", name));
                    };
                    manual.Children.Add(mi);
                }
            }
            root.Children.Add(manual);

            // 最近运行：动态枚举近期记录（只读）
            var recent = new TrayMenuItem
            {
                Id = "task_scheduler_recent",
                Header = Loc.T("Tray.RecentRun", "最近运行")
            };
            var recents = Scheduler != null ? Scheduler.GetRecent() : null;
            if (recents == null || recents.Count == 0)
            {
                recent.Children.Add(new TrayMenuItem
                {
                    Id = "task_scheduler_recent_empty",
                    Header = Loc.T("Tray.NoRecentTasks", "暂无运行记录"),
                    IsEnabled = false
                });
            }
            else
            {
                foreach (var r in recents)
                {
                    recent.Children.Add(new TrayMenuItem
                    {
                        Id = "task_scheduler_recent_" + r,
                        Header = r,
                        IsEnabled = false
                    });
                }
            }
            root.Children.Add(recent);

            root.Children.Add(TrayMenuItem.Separator());

            root.Children.Add(new TrayMenuItem
            {
                Id = "task_scheduler_editor",
                Header = Loc.T("Tray.TaskEditor", "任务编辑器..."),
                ClickAction = () =>
                {
                    try
                    {
                        var win = new CarroDesk.Views.TaskEditorWindow
                        {
                            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen
                        };
                        win.ShowDialog();
                        RequestRefreshSelf();
                    }
                    catch { }
                }
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
