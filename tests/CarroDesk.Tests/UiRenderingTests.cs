using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Linq;
using CarroDesk.Core;
using CarroDesk.Models;
using CarroDesk.Services.Tasks;
using CarroDesk.Modules.ClipboardHistory.Models;
using CarroDesk.Modules.ClipboardHistory.Services;
using CarroDesk.Modules.ClipboardHistory.Views;
using CarroDesk.Modules.TaskScheduler.Views;
using CarroDesk.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class UiRenderingTests
    {
        private class MemoryClipboardStorage : IClipboardHistoryStorage
        {
            public List<ClipboardItem> SavedItems = new List<ClipboardItem>();

            public List<ClipboardItem> Load()
            {
                return new List<ClipboardItem>(SavedItems);
            }

            public void Save(List<ClipboardItem> items)
            {
                SavedItems = new List<ClipboardItem>(items);
            }
        }

        private class FakeTaskSchedulerService : ITaskSchedulerService
        {
            public bool IsGlobalEnabled { get; set; } = true;
            public bool SetGlobalEnabled(bool enabled) { IsGlobalEnabled = enabled; return true; }
            public TaskReloadResult Reload() => new TaskReloadResult();
            public Dictionary<string, TaskStateSnapshot> States = new Dictionary<string, TaskStateSnapshot>(StringComparer.OrdinalIgnoreCase);

            public IReadOnlyList<TaskRunInfo> GetRunning() => States.Values
                .Where(s => s.State == TaskRuntimeState.Running && s.Pid.HasValue)
                .Select(s => new TaskRunInfo { Name = s.TaskName, Pid = s.Pid.Value, StartedAt = s.StartedAt ?? DateTime.Now })
                .ToList();

            public bool IsRunning(string taskName) => States.TryGetValue(taskName, out var s) && s.IsRunning;
            public bool TryStop(string taskName) => true;
            public bool RunManual(string taskName) => true;
            public bool RestartTask(string taskName) => true;
            public TaskStateSnapshot GetState(string taskName) => States.TryGetValue(taskName, out var s) ? s : new TaskStateSnapshot { TaskName = taskName };
            public IReadOnlyDictionary<string, TaskStateSnapshot> GetAllStates() => States;
            public event Action<string, TaskRuntimeState, TaskStateSnapshot> StateChanged { add { } remove { } }
            public TaskSupervisionInfo GetSupervision(string taskName) => new TaskSupervisionInfo();
        }

        private void RunInSta(Action action)
        {
            // 统一使用 TestEnvironment 的 STA 执行器：
            // 它会显式把 Application.ShutdownMode 设为 OnExplicitShutdown，
            // 避免"关闭最后一个窗口"关停 Application 后波及其它 UI 测试。
            TestEnvironment.RunInSta(action);
        }

        private void SaveWindowSnapshot(Window win, double width, double height, string filename)
        {
            win.Width = width;
            win.Height = height;
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.Show();

            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                (Action)(() => { frame.Continue = false; }));
            System.Windows.Threading.Dispatcher.PushFrame(frame);

            Thread.Sleep(150);

            int pxW = (int)Math.Max(1, win.ActualWidth > 0 ? win.ActualWidth : width);
            int pxH = (int)Math.Max(1, win.ActualHeight > 0 ? win.ActualHeight : height);
            var rtb = new RenderTargetBitmap(pxW, pxH, 96, 96, PixelFormats.Pbgra32);
            Visual target = win;
            if (win.AllowsTransparency && win.Content is Visual contentVisual)
            {
                target = contentVisual;
            }
            rtb.Render(target);

            string projectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\.."));
            string dir = Path.Combine(projectRoot, @"temp\screenshots");
            Directory.CreateDirectory(dir);
            string fullPath = Path.Combine(dir, filename);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(fullPath))
            {
                enc.Save(fs);
            }

            win.Close();
        }

        [TestMethod]
        public void Render_ConfigEditorWindow_NoOverlaps_And_SavesSnapshot()
        {
            RunInSta(() =>
            {
                var win = new ConfigEditorWindow(null, null, null);
                SaveWindowSnapshot(win, 580, 720, "ConfigEditorWindow.png");
            });
        }

        [TestMethod]
        public void Render_ClipboardHistoryWindow_NoOverlaps_And_SavesSnapshot()
        {
            RunInSta(() =>
            {
                var storage = new MemoryClipboardStorage();
                var service = new ClipboardHistoryService(storage);
                service.Start(ClipboardHistoryConfig.CreateDefault());
                service.RecordText("第1条多行文本测试\r\n第二行内容预览\r\n第三行内容预览\r\n第四行省略...");
                service.RecordText("第2条短文本 📌 置顶测试");
                service.RecordText("第3条代码片段:\r\nfunction hello() {\r\n    console.log('world');\r\n}");

                var winNormal = new ClipboardHistoryWindow(service);
                winNormal.AutoCloseOnDeactivate = false;
                SaveWindowSnapshot(winNormal, 460, 540, "ClipboardHistoryWindow_Normal.png");

                var winEnlarged = new ClipboardHistoryWindow(service);
                winEnlarged.AutoCloseOnDeactivate = false;
                SaveWindowSnapshot(winEnlarged, 780, 720, "ClipboardHistoryWindow_Enlarged.png");
            });
        }

        [TestMethod]
        public void Render_ClipboardItemDetailWindow_ShowsFullText_And_SavesSnapshot()
        {
            RunInSta(() =>
            {
                var storage = new MemoryClipboardStorage();
                var service = new ClipboardHistoryService(storage);
                service.Start(ClipboardHistoryConfig.CreateDefault());
                service.RecordText("第一行：完整内容窗口应显示全部文本，而不是截断的预览。\r\n第二行：可选中、Ctrl+A 全选、Ctrl+C 复制。\r\n第三行：支持滚动与自动换行切换。");

                var item = service.GetItems()[0];
                var detail = new ClipboardItemDetailWindow(service);
                detail.LoadItem(item);
                SaveWindowSnapshot(detail, 720, 560, "ClipboardItemDetailWindow.png");
            });
        }

        [TestMethod]
        public void ClipboardItemDetailWindow_LoadItem_KeepsFullText_And_MetaInfo()
        {
            RunInSta(() =>
            {
                var storage = new MemoryClipboardStorage();
                var service = new ClipboardHistoryService(storage);
                service.Start(ClipboardHistoryConfig.CreateDefault());

                const string raw = "line1\nline2\nline3";
                service.RecordText(raw);

                var item = service.GetItems()[0];
                var detail = new ClipboardItemDetailWindow(service);
                detail.LoadItem(item);

                // 预览被截断，但详情窗口必须持有完整原文
                Assert.AreEqual(raw, item.FullText);
                Assert.IsTrue(detail.FullText.Length >= detail.DisplayText.Length,
                    "详情窗口渲染文本不应超过原文长度");
                Assert.AreEqual(raw, detail.FullText, "详情窗口必须保留完整原文");
                Assert.IsTrue(detail.MetaInfo.Contains("3"), "元信息应包含字符数: " + detail.MetaInfo);
                Assert.IsTrue(detail.MetaInfo.Contains("3"), "元信息应包含行数: " + detail.MetaInfo);
                Assert.IsFalse(detail.IsTruncated, "普通长度文本不应显示截断提示");

                detail.Close();
            });
        }

        [TestMethod]
        public void Render_TaskManagerWindow_SavesSnapshot()
        {
            RunInSta(() =>
            {
                var sampleTasks = new List<TaskDefinition>
                {
                    new TaskDefinition
                    {
                        Name = "db-backup-daily",
                        Enabled = true,
                        Trigger = new TaskTrigger { Type = TaskTriggerType.Daily, At = "03:00" },
                        Action = new TaskAction { File = "backup.bat", Args = "--compress --target=D:\\Backups" },
                        Options = new TaskOptions { Mode = "wait", TimeoutSec = 3600, CatchUpMissed = true }
                    },
                    new TaskDefinition
                    {
                        Name = "downloads-watcher",
                        Enabled = true,
                        Trigger = new TaskTrigger { Type = TaskTriggerType.Watch, WatchPath = @"D:\Downloads", WatchFilter = "*.zip;*.tar.gz" },
                        Action = new TaskAction { File = "unpack.py", Args = "{{file}}" },
                        Options = new TaskOptions { Mode = "wait" }
                    },
                    new TaskDefinition
                    {
                        Name = "cache-cleaner",
                        Enabled = true,
                        Trigger = new TaskTrigger { Type = TaskTriggerType.Interval, Every = "30m" },
                        Action = new TaskAction { File = "clean-temp.ps1" },
                        Options = new TaskOptions { Mode = "wait", Retry = 3 }
                    },
                    new TaskDefinition
                    {
                        Name = "nginx-daemon",
                        Enabled = true,
                        Trigger = new TaskTrigger { Type = TaskTriggerType.Startup, DelaySec = 5 },
                        Action = new TaskAction { File = @"C:\nginx\nginx.exe" },
                        Options = new TaskOptions { Mode = "detach", Restart = "on-failure", RestartDelaySec = 5, RestartLimit = 5 }
                    },
                    new TaskDefinition
                    {
                        Name = "compile-project",
                        Enabled = true,
                        Trigger = new TaskTrigger { Type = TaskTriggerType.Manual },
                        Action = new TaskAction { File = "dotnet", Args = "build --configuration Release" },
                        Options = new TaskOptions { Mode = "wait" }
                    },
                    new TaskDefinition
                    {
                        Name = "legacy-sync-agent",
                        Enabled = false,
                        Trigger = new TaskTrigger { Type = TaskTriggerType.Interval, Every = "1h" },
                        Action = new TaskAction { File = "sync.cmd" }
                    }
                };

                TaskConfigService.Save(sampleTasks);

                var scheduler = new FakeTaskSchedulerService();
                scheduler.States["nginx-daemon"] = new TaskStateSnapshot
                {
                    TaskName = "nginx-daemon",
                    State = TaskRuntimeState.Running,
                    Pid = 6214,
                    StartedAt = DateTime.Now.AddSeconds(-382)
                };
                scheduler.States["cache-cleaner"] = new TaskStateSnapshot
                {
                    TaskName = "cache-cleaner",
                    State = TaskRuntimeState.WaitingRetry,
                    ConsecutiveFailures = 1,
                    NextRetryDelaySec = 4,
                    LastOutcome = "exit code 1 (timeout)"
                };
                scheduler.States["compile-project"] = new TaskStateSnapshot
                {
                    TaskName = "compile-project",
                    State = TaskRuntimeState.MarkedFailed,
                    ConsecutiveFailures = 5,
                    LastExitCode = 2,
                    LastOutcome = "error: build failed"
                };

                var win = new TaskManagerWindow(scheduler);
                SaveWindowSnapshot(win, 980, 620, "TaskManagerWindow.png");
            });
        }

        [TestMethod]
        public void Render_TaskEditDialog_EditMode_SavesSnapshot()
        {
            RunInSta(() =>
            {
                var task = new TaskDefinition
                {
                    Name = "db-backup-daily",
                    Enabled = true,
                    Trigger = new TaskTrigger { Type = TaskTriggerType.Daily, At = "03:00" },
                    Action = new TaskAction { File = "backup.bat", Args = "--compress --target=D:\\Backups" },
                    Options = new TaskOptions { Mode = "wait", TimeoutSec = 3600, CatchUpMissed = true }
                };

                var win = new TaskEditDialog(task, isNew: false, new[] { "db-backup-daily", "nginx-daemon" });
                SaveWindowSnapshot(win, 740, 680, "TaskEditDialog_EditMode.png");
            });
        }

        [TestMethod]
        public void Render_TaskEditDialog_NewMode_SavesSnapshot()
        {
            RunInSta(() =>
            {
                var win = new TaskEditDialog(null, isNew: true, new[] { "db-backup-daily", "nginx-daemon" });
                SaveWindowSnapshot(win, 740, 680, "TaskEditDialog_NewMode.png");
            });
        }

        [TestMethod]
        public void XamlLayout_AllGrids_HaveSufficientRowAndColumnDefinitions()
        {
            // 自基线目录逐级上溯，直到找到同时含 src/ 与 tests/ 的项目根。
            // 固定上溯层数不可靠：输出路径深度会随平台目录（bin\Any CPU\Debug\net48）
            // 与配置变化，多退或少退一级都会让 Directory.GetFiles 抛 DirectoryNotFoundException。
            string projectRoot = null;
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src"))
                    && Directory.Exists(Path.Combine(dir.FullName, "tests")))
                {
                    projectRoot = dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }

            Assert.IsNotNull(projectRoot, "未能从基线目录定位到项目根（应包含 src/ 与 tests/）");

            string srcDir = Path.Combine(projectRoot, "src");
            var xamlFiles = Directory.GetFiles(srcDir, "*.xaml", SearchOption.AllDirectories);

            var errors = new List<string>();
            foreach (var file in xamlFiles)
            {
                string text = File.ReadAllText(file);
                var gridMatches = Regex.Matches(text, @"<Grid\b.*?</Grid>", RegexOptions.Singleline);
                for (int i = 0; i < gridMatches.Count; i++)
                {
                    string g = gridMatches[i].Value;
                    int rowDefs = Regex.Matches(g, @"<RowDefinition\b").Count;
                    int colDefs = Regex.Matches(g, @"<ColumnDefinition\b").Count;

                    var rowMatches = Regex.Matches(g, @"Grid\.Row=""(\d+)""");
                    int maxRow = 0;
                    foreach (Match m in rowMatches)
                    {
                        if (int.TryParse(m.Groups[1].Value, out int r) && r > maxRow) maxRow = r;
                    }

                    var colMatches = Regex.Matches(g, @"Grid\.Column=""(\d+)""");
                    int maxCol = 0;
                    foreach (Match m in colMatches)
                    {
                        if (int.TryParse(m.Groups[1].Value, out int c) && c > maxCol) maxCol = c;
                    }

                    if (rowDefs > 0 && maxRow >= rowDefs)
                    {
                        errors.Add($"File: {Path.GetFileName(file)}, Grid #{i}: defined {rowDefs} rows, but element uses Grid.Row=\"{maxRow}\"");
                    }
                    if (colDefs > 0 && maxCol >= colDefs)
                    {
                        errors.Add($"File: {Path.GetFileName(file)}, Grid #{i}: defined {colDefs} cols, but element uses Grid.Column=\"{maxCol}\"");
                    }
                }
            }

            Assert.AreEqual(0, errors.Count, "Grid definitions overflow found:\n" + string.Join("\n", errors));
        }
    }
}
