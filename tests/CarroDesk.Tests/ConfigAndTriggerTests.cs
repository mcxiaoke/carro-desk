using System;
using System.IO;
using System.Threading;
using CarroDesk.Common;
using CarroDesk.Models;
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
                IdleMinutes = 15,
                AutoStart = false,
                ShowClock = false,
                OverlayOpacity = 0.5,
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

            Assert.AreEqual(15, cloned.IdleMinutes);
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
    }
}
