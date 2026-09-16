using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenLock.Models;
using ScreenLock.Services;

namespace ScreenLock.Views
{
    public partial class TrayContextMenu : ContextMenu
    {
        public TrayContextMenu()
        {
            InitializeComponent();
            Opened += (s, e) => RefreshAll();
        }

        public void RefreshAll()
        {
            RefreshStatus();
            RefreshChecks();
            RefreshTaskSubmenu();
        }

        public void SetStatus(string detail, string badgeText, Brush dotBrush, Brush badgeBg, Brush badgeFg)
        {
            try
            {
                if (StatusDot != null) StatusDot.Fill = dotBrush;
                if (StatusDetailText != null) StatusDetailText.Text = detail;
                if (StatusBadge != null)
                {
                    StatusBadge.Text = badgeText;
                    StatusBadge.Foreground = badgeFg;
                }
                if (StatusBadgeBorder != null) StatusBadgeBorder.Background = badgeBg;
            }
            catch { }
        }

        public void RefreshStatus()
        {
            try
            {
                var app = App.CurrentApp;
                var config = App.Config?.Current;
                var controller = App.Controller;

                if (controller != null && controller.IsLocked)
                {
                    SetStatus("当前屏幕已锁定", "已锁定",
                        new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                        new SolidColorBrush(Color.FromRgb(0xFE, 0xF3, 0xC7)),
                        new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)));
                }
                else if (app != null && app.IsPaused)
                {
                    SetStatus(string.Format("已暂停锁定至 {0:HH:mm}", app.PauseUntil), "已暂停",
                        new SolidColorBrush(Color.FromRgb(0xE1, 0x98, 0x05)),
                        new SolidColorBrush(Color.FromRgb(0xFF, 0xFB, 0xEB)),
                        new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)));
                }
                else if (config == null || config.IdleMinutes <= 0)
                {
                    SetStatus("空闲锁定已禁用", "已禁用",
                        new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
                        new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)),
                        new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)));
                }
                else
                {
                    SetStatus(string.Format("空闲 {0} 分钟后锁定", config.IdleMinutes), "运行中",
                        new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
                        new SolidColorBrush(Color.FromRgb(0xEC, 0xFD, 0xF5)),
                        new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69)));
                }
            }
            catch { }
        }

        public void RefreshChecks()
        {
            try
            {
                var config = App.Config?.Current;
                if (config != null)
                {
                    if (IdleMenu != null)
                    {
                        foreach (var item in IdleMenu.Items)
                        {
                            if (item is MenuItem mi && mi.Tag != null && int.TryParse(mi.Tag.ToString(), out int mins))
                            {
                                mi.IsChecked = (mins == config.IdleMinutes);
                            }
                        }
                    }

                    if (AutoStartItem != null)
                    {
                        AutoStartItem.IsChecked = config.AutoStart;
                    }

                    if (TasksEnabledItem != null)
                    {
                        TasksEnabledItem.IsChecked = App.TaskScheduler != null && App.TaskScheduler.IsGlobalEnabled;
                    }
                }
            }
            catch { }
        }

        public void RefreshTaskSubmenu()
        {
            try
            {
                if (ManualMenu != null)
                {
                    ManualMenu.Items.Clear();
                    var scheduler = App.TaskScheduler;
                    var manuals = scheduler != null ? scheduler.GetManualTasks() : null;

                    if (manuals == null || manuals.Count == 0)
                    {
                        ManualMenu.Items.Add(new MenuItem { Header = "暂无手动任务", IsEnabled = false });
                    }
                    else
                    {
                        foreach (var t in manuals)
                        {
                            var name = t.Name;
                            var item = new MenuItem { Header = name };
                            if (t.Trigger != null && t.Trigger.Type == TaskTriggerType.Hotkey && !string.IsNullOrWhiteSpace(t.Trigger.Hotkey))
                            {
                                item.InputGestureText = t.Trigger.Hotkey;
                            }
                            item.Click += (s, e) =>
                            {
                                if (App.TaskScheduler != null)
                                {
                                    bool ok = App.TaskScheduler.RunManual(name);
                                    App.ShowBalloonPublic(ok ? "已触发任务: " + name : "任务未找到或已禁用: " + name);
                                }
                            };
                            ManualMenu.Items.Add(item);
                        }
                    }
                }

                if (RecentMenu != null)
                {
                    RecentMenu.Items.Clear();
                    var scheduler = App.TaskScheduler;
                    var recents = scheduler != null ? scheduler.GetRecent() : null;

                    if (recents == null || recents.Count == 0)
                    {
                        RecentMenu.Items.Add(new MenuItem { Header = "暂无记录", IsEnabled = false });
                    }
                    else
                    {
                        foreach (var r in recents)
                        {
                            RecentMenu.Items.Add(new MenuItem { Header = r, IsEnabled = false });
                        }
                    }
                }
            }
            catch { }
        }

        private void OnLockNowClick(object sender, RoutedEventArgs e)
        {
            App.Controller?.LockSafe();
        }

        private void OnIdlePresetClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag != null && int.TryParse(mi.Tag.ToString(), out int mins))
            {
                App.CurrentApp?.SetIdleMinutes(mins);
            }
        }

        private void OnPause30Click(object sender, RoutedEventArgs e)
        {
            App.CurrentApp?.PauseFor(TimeSpan.FromMinutes(30));
        }

        private void OnPause60Click(object sender, RoutedEventArgs e)
        {
            App.CurrentApp?.PauseFor(TimeSpan.FromHours(1));
        }

        private void OnResumeIdleClick(object sender, RoutedEventArgs e)
        {
            App.CurrentApp?.ResumeIdle();
        }

        private void OnAutoStartClick(object sender, RoutedEventArgs e)
        {
            if (App.Config?.Current != null)
            {
                bool newState = !App.Config.Current.AutoStart;
                App.Config.Current.AutoStart = newState;
                AutoStartService.Sync(newState);
                App.Config.Save();
                RefreshChecks();
            }
        }

        private void OnConfigEditorClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new ConfigEditorWindow
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                win.ShowDialog();
                App.CurrentApp?.RefreshMenuChecks();
                App.CurrentApp?.UpdateTrayText();
                RefreshAll();
            }
            catch { }
        }

        private void OnTasksEnabledToggleClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.TaskScheduler != null && App.Config?.Current != null)
                {
                    bool enabled = !App.TaskScheduler.IsGlobalEnabled;
                    App.TaskScheduler.SetGlobalEnabled(enabled);
                    App.Config.Current.TasksEnabled = enabled;
                    App.Config.Save();
                    App.ShowBalloonPublic(enabled ? "任务调度已启用" : "任务调度已禁用");
                    RefreshChecks();
                }
            }
            catch { }
        }

        private void OnTaskEditorClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new TaskEditorWindow
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                win.ShowDialog();
                RefreshTaskSubmenu();
            }
            catch { }
        }

        private void OnReloadTasksClick(object sender, RoutedEventArgs e)
        {
            App.CurrentApp?.ReloadTasks();
        }

        private void OnOpenConfigDirClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(ConfigService.DirPath);
            }
            catch { }
        }

        private void OnReloadConfigClick(object sender, RoutedEventArgs e)
        {
            App.CurrentApp?.ReloadConfig();
        }

        private void OnExitClick(object sender, RoutedEventArgs e)
        {
            App.CurrentApp?.PromptExit();
        }
    }
}
