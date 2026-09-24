using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using CarroDesk.Common;
using CarroDesk.Core;
using CarroDesk.Models;
using CarroDesk.Modules.TaskScheduler.Models;
using CarroDesk.Services.Tasks;
using CarroDesk.Services.Tasks.Triggers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class ConfigAndTriggerTests
    {
        [TestMethod]
        public void AtomicFile_WriteAllText_CreatesAndOverwritesSafely()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "CarroDesk_Test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string targetFile = Path.Combine(tempDir, "sub", "config.json");

                // 首次写入（父目录自动创建）
                AtomicFile.WriteAllText(targetFile, "{\"content\":1}");
                Assert.IsTrue(File.Exists(targetFile));
                Assert.AreEqual("{\"content\":1}", File.ReadAllText(targetFile));

                // 覆写
                AtomicFile.WriteAllText(targetFile, "{\"content\":2}");
                Assert.AreEqual("{\"content\":2}", File.ReadAllText(targetFile));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [TestMethod]
        public void AppSettings_CloneAndCopy_PreservesFloatingPanelAndPin()
        {
            var original = new AppSettings
            {
                AutoStart = false,
                PinSalt = "salt_123",
                PinHash = "hash_456",
                FloatingPanelHotkey = "Ctrl+Shift+F",
                FloatingPanelPosition = "TopRight",
                FloatingPanelPinned = true,
                FloatingPanelLocked = true,
                FloatingPanelX = 250,
                FloatingPanelY = 350
            };

            var cloned = original.Clone();

            Assert.IsFalse(cloned.AutoStart);
            Assert.AreEqual("salt_123", cloned.PinSalt);
            Assert.AreEqual("hash_456", cloned.PinHash);
            Assert.AreEqual("Ctrl+Shift+F", cloned.FloatingPanelHotkey);
            Assert.AreEqual("TopRight", cloned.FloatingPanelPosition);
            Assert.IsTrue(cloned.FloatingPanelPinned);
            Assert.IsTrue(cloned.FloatingPanelLocked);
            Assert.AreEqual(250, cloned.FloatingPanelX);
            Assert.AreEqual(350, cloned.FloatingPanelY);
        }

        [TestMethod]
        public void ScreenLockConfig_Clone_PreservesProperties()
        {
            var original = new CarroDesk.Modules.ScreenLock.Models.ScreenLockConfig
            {
                IdleMinutes = 15,
                ShowClock = false,
                OverlayOpacity = 0.5,
                Hotkey = "Ctrl+Shift+L",
                UnlockOnResume = false,
                ExcludeProcesses = new System.Collections.Generic.List<string> { "game.exe" }
            };

            var cloned = original.Clone();

            Assert.AreEqual(15, cloned.IdleMinutes);
            Assert.IsFalse(cloned.ShowClock);
            Assert.AreEqual(0.5, cloned.OverlayOpacity);
            Assert.AreEqual("Ctrl+Shift+L", cloned.Hotkey);
            Assert.IsFalse(cloned.UnlockOnResume);
            Assert.AreEqual(1, cloned.ExcludeProcesses.Count);
            Assert.AreEqual("game.exe", cloned.ExcludeProcesses[0]);
        }

        [TestMethod]
        public void FileWatcherTrigger_PerFileDebounce_AllowsDifferentFilesConcurrently()
        {
            var task = new TaskDefinition { Name = "test_watch", Trigger = new TaskTrigger { Type = TaskTriggerType.Watch } };
            var trigger = new FileWatcherTrigger(task);
            var now = DateTime.Now;

            // 1. 同一文件 500ms 内第 1 次允许，第 2、3 次被防抖拦截
            Assert.IsFalse(trigger.ShouldDebounce("C:\\test\\file1.txt", now));
            Assert.IsTrue(trigger.ShouldDebounce("C:\\test\\file1.txt", now.AddMilliseconds(50)));
            Assert.IsTrue(trigger.ShouldDebounce("C:\\test\\file1.txt", now.AddMilliseconds(200)));

            // 2. 500ms 内同时到达的不同文件，必须全部放行，绝不能被共享字段吞掉
            for (int i = 2; i <= 10; i++)
            {
                string path = "C:\\test\\file" + i + ".txt";
                Assert.IsFalse(trigger.ShouldDebounce(path, now.AddMilliseconds(10)), "文件 " + path + " 应当被正常触发放行");
            }

            // 3. 超过 500ms 后，同一文件再次触发应放行
            Assert.IsFalse(trigger.ShouldDebounce("C:\\test\\file1.txt", now.AddMilliseconds(600)));
        }

        private class FakeConfigManager : IConfigManager
        {
            public Dictionary<string, object> Store = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            public event Action ConfigReloaded;

            public T GetModuleConfig<T>(string moduleId) where T : class, new()
            {
                if (Store.TryGetValue(moduleId, out var val) && val is T typed) return typed;
                var created = new T();
                Store[moduleId] = created;
                return created;
            }

            public void SaveModuleConfig<T>(string moduleId, T config) where T : class
            {
                Store[moduleId] = config;
            }

            public void Reload() => ConfigReloaded?.Invoke();
        }

        private class FakeNotificationService : INotificationService
        {
            public List<string> Messages = new List<string>();
            public void Show(string message, string title = "CarroDesk")
            {
                Messages.Add(message);
            }
        }

        [TestMethod]
        public void TaskSchedulerService_CanRunStandaloneWithoutAppStaticFacade()
        {
            // 验收标准 P1-2：TaskSchedulerService 零 App. 引用且可脱离 App 单测
            var fakeCfg = new FakeConfigManager();
            var fakeNotif = new FakeNotificationService();
            string balloonMsg = null;

            using (var scheduler = new TaskSchedulerService(
                idleService: null,
                configManager: fakeCfg,
                notificationService: fakeNotif,
                balloonNotifier: msg => balloonMsg = msg))
            {
                scheduler.Start();
                Assert.IsTrue(scheduler.IsGlobalEnabled);

                scheduler.SetGlobalEnabled(false);
                Assert.IsFalse(scheduler.IsGlobalEnabled);

                var savedCfg = fakeCfg.GetModuleConfig<TaskSchedulerConfig>("TaskScheduler");
                Assert.IsFalse(savedCfg.GlobalEnabled);

                scheduler.SetGlobalEnabled(true);
                Assert.IsTrue(scheduler.IsGlobalEnabled);
                Assert.IsTrue(savedCfg.GlobalEnabled);
            }
        }

        [TestMethod]
        public void ViewLayer_HasNoStaticAppReferences()
        {
            // 验收标准 P1-2：grep "App\." src --include=*.cs 在视图层与业务模块降至 0
            string solutionRoot = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(solutionRoot) && 
                   !File.Exists(Path.Combine(solutionRoot, "CarroDesk.slnx")) && 
                   !File.Exists(Path.Combine(solutionRoot, "Directory.Build.props")) && 
                   !Directory.Exists(Path.Combine(solutionRoot, ".git")))
            {
                var parent = Directory.GetParent(solutionRoot);
                if (parent == null) break;
                solutionRoot = parent.FullName;
            }

            string srcDir = Path.Combine(solutionRoot, "src");
            Assert.IsTrue(Directory.Exists(srcDir), $"未能定位到项目 src 目录，当前探索根路径: {solutionRoot}");

            var csFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories)
                .Where(f =>
                {
                    string normalized = f.Replace('\\', '/');
                    return !normalized.Contains("/Host/")
                        && !normalized.Contains("/Core/")
                        && !normalized.Contains("/obj/")
                        && !normalized.Contains("/bin/")
                        && !normalized.EndsWith("/App.xaml.cs");
                })
                .ToList();

            Assert.IsTrue(csFiles.Count > 10, $"扫描到的业务/视图代码文件过少 ({csFiles.Count})，请检查路径过滤逻辑");

            var regex = new Regex(@"\bApp\.(Config|Modules|Services|CurrentApp|ScreenLockMod|TaskSchedulerMod|AppAutoMuteMod|MonitorProfileMod|AwakeMod|IsShuttingDown)\b");
            foreach (var file in csFiles)
            {
                string text = File.ReadAllText(file);
                var matches = regex.Matches(text);
                Assert.AreEqual(0, matches.Count, $"业务与视图文件 {Path.GetFileName(file)} 中不应存在 App.* 静态门面引用！匹配数: {matches.Count}");
            }
        }

        [TestMethod]
        public void AutoStartService_GetCurrentExecutablePath_ReturnsValidPath()
        {
            string exe = CarroDesk.Services.AutoStartService.GetCurrentExecutablePath();
            Assert.IsFalse(string.IsNullOrWhiteSpace(exe), "可执行文件路径不应为空");
            Assert.IsTrue(File.Exists(exe), "可执行文件应真实存在于磁盘上: " + exe);
        }
    }
}
