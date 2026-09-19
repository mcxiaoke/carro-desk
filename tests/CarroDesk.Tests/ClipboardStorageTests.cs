using System;
using System.Collections.Generic;
using System.IO;
using CarroDesk.Modules.ClipboardHistory.Models;
using CarroDesk.Modules.ClipboardHistory.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 剪贴板 JSON 存储的写入语义测试（P1-7）。
    /// </summary>
    [TestClass]
    public class ClipboardStorageTests
    {
        private static string NewPath()
        {
            return Path.Combine(TestEnvironment.TempRoot, "clip-" + Guid.NewGuid().ToString("N") + ".json");
        }

        [TestMethod]
        public void Save_ThenFlush_RoundTripsItems()
        {
            string path = NewPath();
            using (var storage = new JsonClipboardHistoryStorage(path))
            {
                storage.Save(new List<ClipboardItem>
                {
                    new ClipboardItem { FullText = "alpha", TextLength = 5, IsPinned = true },
                    new ClipboardItem { FullText = "beta", TextLength = 4 }
                });
                storage.Flush();

                Assert.IsTrue(File.Exists(path), "Flush 后文件应已落盘");

                var loaded = storage.Load();
                Assert.AreEqual(2, loaded.Count);
                Assert.AreEqual("alpha", loaded[0].FullText);
                Assert.IsTrue(loaded[0].IsPinned);
                Assert.AreEqual("beta", loaded[1].FullText);
            }
        }

        [TestMethod]
        public void Save_IsolatesSnapshotFromLaterMutation()
        {
            string path = NewPath();
            using (var storage = new JsonClipboardHistoryStorage(path))
            {
                var live = new List<ClipboardItem>
                {
                    new ClipboardItem { FullText = "first", TextLength = 5 }
                };

                storage.Save(live);

                // Save 之后立刻改动原列表（模拟服务继续在 UI 线程写入）
                live[0].FullText = "mutated-after-save";
                live.Add(new ClipboardItem { FullText = "second", TextLength = 6 });

                storage.Flush();

                var loaded = storage.Load();
                Assert.AreEqual(1, loaded.Count, "写盘应基于 Save 时刻的快照，而不是之后被改动的列表");
                Assert.AreEqual("first", loaded[0].FullText, "异步写盘读到了后续被修改的值");
            }
        }

        [TestMethod]
        public void Save_MultipleTimes_CoalescesToLatestSnapshot()
        {
            string path = NewPath();
            using (var storage = new JsonClipboardHistoryStorage(path))
            {
                storage.Save(new List<ClipboardItem> { new ClipboardItem { FullText = "v1", TextLength = 2 } });
                storage.Save(new List<ClipboardItem> { new ClipboardItem { FullText = "v2", TextLength = 2 } });
                storage.Save(new List<ClipboardItem> { new ClipboardItem { FullText = "v3", TextLength = 2 } });
                storage.Flush();

                var loaded = storage.Load();
                Assert.AreEqual(1, loaded.Count);
                Assert.AreEqual("v3", loaded[0].FullText, "合并写入应保留最后一版快照");
            }
        }

        [TestMethod]
        public void Load_OnMissingFile_ReturnsEmptyList()
        {
            using (var storage = new JsonClipboardHistoryStorage(NewPath()))
            {
                Assert.AreEqual(0, storage.Load().Count);
            }
        }

        [TestMethod]
        public void Save_OverwritesExistingFile_WithoutLosingContent()
        {
            string path = NewPath();
            using (var storage = new JsonClipboardHistoryStorage(path))
            {
                storage.Save(new List<ClipboardItem> { new ClipboardItem { FullText = "old", TextLength = 3 } });
                storage.Flush();
                Assert.AreEqual("old", storage.Load()[0].FullText);

                storage.Save(new List<ClipboardItem> { new ClipboardItem { FullText = "new", TextLength = 3 } });
                storage.Flush();
                Assert.AreEqual("new", storage.Load()[0].FullText);
            }
        }
    }
}
