using System;
using System.Collections.Generic;
using System.Linq;
using CarroDesk.Core;
using CarroDesk.Host.Services;
using CarroDesk.Modules.ClipboardHistory;
using CarroDesk.Modules.ClipboardHistory.Models;
using CarroDesk.Modules.ClipboardHistory.Services;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class ClipboardHistoryModuleTests
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

        [TestMethod]
        public void ClipboardHistoryConfig_Defaults_AreValid()
        {
            var config = ClipboardHistoryConfig.CreateDefault();
            Assert.IsTrue(config.Enabled);
            Assert.IsTrue(config.AutoRecord);
            Assert.AreEqual("Win+Alt+V", config.Hotkey);
            Assert.AreEqual(100, config.MaxPreviewChars);
            Assert.AreEqual(1000, config.MaxItems);
            Assert.AreEqual(90, config.RetentionDays);
        }

        [TestMethod]
        public void ClipboardHistoryModule_Metadata_FollowsModuleContract()
        {
            var module = new ClipboardHistoryModule();
            Assert.AreEqual("ClipboardHistory", module.Id);
            Assert.AreEqual(35, module.Order);
            Assert.IsTrue(module.DefaultEnabled);
            Assert.IsFalse(string.IsNullOrWhiteSpace(module.Name));
            Assert.IsFalse(string.IsNullOrWhiteSpace(module.Description));
        }

        [TestMethod]
        public void ClipboardHistoryModule_GetTrayMenuItems_StrictlyFollowsTwoLevelDesign()
        {
            var module = new ClipboardHistoryModule();
            var items = module.GetTrayMenuItems()?.ToList();

            Assert.IsNotNull(items);
            Assert.AreEqual(1, items.Count, "ClipboardHistoryModule 必须严格遵守单一根节点收敛契约");

            var root = items[0];
            Assert.AreEqual("clipboard_root", root.Id);
            Assert.IsTrue(root.Header.Contains("剪贴板历史"));

            Assert.IsTrue(root.Children.Any(c => c.Id == "clipboard_open"), "应包含打开窗口操作");
            Assert.IsTrue(root.Children.Any(c => c.Id == "clipboard_auto_record"), "应包含自动记录开关");
            Assert.IsTrue(root.Children.Any(c => c.Id == "clipboard_clear_all"), "应包含清空记录操作");
        }

        [TestMethod]
        public void ClipboardHistory_TruncationRule_TruncatesPreview_PreservesFullText()
        {
            var storage = new MemoryClipboardStorage();
            var service = new ClipboardHistoryService(storage);
            var config = new ClipboardHistoryConfig
            {
                MaxPreviewChars = 20,
                MaxItems = 100,
                RetentionDays = 30
            };

            service.Start(config);

            string longText = "这是一段非常长长长长长长长长长长长长长长长长长长长长长长长长的文本\r\n换行测试";
            service.RecordText(longText);

            var items = service.GetItems();
            Assert.AreEqual(1, items.Count);

            var item = items[0];
            Assert.AreEqual(longText, item.FullText, "完整文本必须完全保留以供重新复制");
            Assert.IsTrue(item.PreviewText.EndsWith("..."), "预览文本必须截断并附带省略号");
            Assert.IsTrue(item.PreviewText.Length <= 23, "截断后长度应受限");
            Assert.IsFalse(item.PreviewText.Contains("\r"), "预览文本不应包含回车换行");
            Assert.AreEqual(longText.Length, item.TextLength);
        }

        [TestMethod]
        public void ClipboardHistory_MaxItemsRetention_EvictsOldestItems()
        {
            var storage = new MemoryClipboardStorage();
            var service = new ClipboardHistoryService(storage);
            var config = new ClipboardHistoryConfig
            {
                MaxItems = 3,
                RetentionDays = 30
            };

            service.Start(config);

            service.RecordText("第1条文本");
            service.RecordText("第2条文本");
            service.RecordText("第3条文本");
            service.RecordText("第4条文本");
            service.RecordText("第5条文本");

            var items = service.GetItems();
            Assert.AreEqual(3, items.Count, "历史总数应被严格限制在 MaxItems 内");
            Assert.AreEqual("第5条文本", items[0].FullText, "最新录入的应在第一位");
            Assert.AreEqual("第4条文本", items[1].FullText);
            Assert.AreEqual("第3条文本", items[2].FullText);
            Assert.AreEqual(3, storage.SavedItems.Count, "持久化存储也应同步更新");
        }

        [TestMethod]
        public void ClipboardHistory_ExpirationRetention_RemovesExpiredItems()
        {
            var storage = new MemoryClipboardStorage();
            // 预设一条 100 天前的数据
            storage.SavedItems.Add(new ClipboardItem
            {
                FullText = "超期老数据",
                PreviewText = "超期老数据",
                CopiedAt = DateTime.Now.AddDays(-100),
                Hash = "old_hash"
            });
            storage.SavedItems.Add(new ClipboardItem
            {
                FullText = "正常新数据",
                PreviewText = "正常新数据",
                CopiedAt = DateTime.Now.AddDays(-10),
                Hash = "new_hash"
            });

            var service = new ClipboardHistoryService(storage);
            var config = new ClipboardHistoryConfig
            {
                MaxItems = 100,
                RetentionDays = 90
            };

            service.Start(config);

            var items = service.GetItems();
            Assert.AreEqual(1, items.Count, "超过 90 天的数据启动时应自动被清理剔除");
            Assert.AreEqual("正常新数据", items[0].FullText);
        }

        [TestMethod]
        public void ClipboardHistory_Deduplication_MovesExistingToTop()
        {
            var storage = new MemoryClipboardStorage();
            var service = new ClipboardHistoryService(storage);
            service.Start(new ClipboardHistoryConfig { MaxItems = 10 });

            service.RecordText("Item A");
            service.RecordText("Item B");
            service.RecordText("Item C");

            Assert.AreEqual(3, service.GetItems().Count);
            Assert.AreEqual("Item C", service.GetItems()[0].FullText);

            // 再次复制 Item A，应当提升至栈顶，且总数仍然是 3
            service.RecordText("Item A");

            var items = service.GetItems();
            Assert.AreEqual(3, items.Count, "重复录入不应增加条目总数");
            Assert.AreEqual("Item A", items[0].FullText, "重复录入应提升到第 1 位 (MRU)");
            Assert.AreEqual("Item C", items[1].FullText);
            Assert.AreEqual("Item B", items[2].FullText);
        }

        [TestMethod]
        public void ClipboardHistory_SelfFeedbackLoop_IsSuppressed()
        {
            var storage = new MemoryClipboardStorage();
            var service = new ClipboardHistoryService(storage);
            service.Start(new ClipboardHistoryConfig { MaxItems = 10 });

            service.RecordText("原始条目");
            Assert.AreEqual(1, service.Count);

            // 模拟用户在窗口双击某条目，执行回写剪贴板前通知抑制
            string textToCopy = "原始条目";
            service.SuppressNext(textToCopy);

            // 紧接着系统发来剪切板变更广播
            service.RecordText(textToCopy);

            // 验证未发生重复录入或时间刷新变更
            Assert.AreEqual(1, service.Count);
        }

        [TestMethod]
        public void ClipboardHistory_ClearAndRemoveItem()
        {
            var storage = new MemoryClipboardStorage();
            var service = new ClipboardHistoryService(storage);
            service.Start(new ClipboardHistoryConfig());

            service.RecordText("Text 1");
            service.RecordText("Text 2");
            Assert.AreEqual(2, service.Count);

            var itemToDelete = service.GetItems()[0];
            service.RemoveItem(itemToDelete.Id);
            Assert.AreEqual(1, service.Count);
            Assert.AreEqual("Text 1", service.GetItems()[0].FullText);

            service.ClearAll(preservePinned: false);
            Assert.AreEqual(0, service.Count);
            Assert.AreEqual(0, storage.SavedItems.Count);
        }

        [TestMethod]
        public void ClipboardHistory_Pin_PreservesPinnedOnClearAndPrioritizesOnTop()
        {
            var storage = new MemoryClipboardStorage();
            var service = new ClipboardHistoryService(storage);
            service.Start(new ClipboardHistoryConfig { MaxItems = 3 });

            service.RecordText("Text 1");
            service.RecordText("Text 2");
            Assert.AreEqual(2, service.Count);

            // Pin 第 1 个（Text 2）
            var itemToPin = service.GetItems().First(x => x.FullText == "Text 1");
            service.TogglePin(itemToPin.Id);

            var items = service.GetItems();
            Assert.IsTrue(items[0].IsPinned, "置顶条目应当排在最前");
            Assert.AreEqual("Text 1", items[0].FullText);

            // 再次添加新项，置顶项仍然排在最前面
            service.RecordText("Text 3");
            items = service.GetItems();
            Assert.AreEqual("Text 1", items[0].FullText, "新增项目后已置顶项依然在最顶端");
            Assert.IsTrue(items[0].IsPinned);

            // ClearAll(preservePinned: true) 默认保留置顶项
            service.ClearAll(preservePinned: true);
            Assert.AreEqual(1, service.Count, "清空未固定项后应保留已置顶条目");
            Assert.AreEqual("Text 1", service.GetItems()[0].FullText);

            // 彻底清空
            service.ClearAll(preservePinned: false);
            Assert.AreEqual(0, service.Count);
        }

        [TestMethod]
        public void ClipboardHistory_Config_DirectSerializationRoundTrip()
        {
            var configService = new ConfigService();
            configService.LoadOrCreate();

            var configMgr = new ConfigManager(configService);
            var module = new ClipboardHistoryModule();

            var cfg = new ClipboardHistoryConfig
            {
                Enabled = true,
                AutoRecord = false,
                Hotkey = "Ctrl+Shift+V",
                MaxPreviewChars = 80,
                MaxItems = 500,
                RetentionDays = 60
            };

            configMgr.SaveModuleConfig("ClipboardHistory", cfg);
            Assert.IsNotNull(configService.GetModuleToken("ClipboardHistory"));

            var reloaded = configMgr.GetModuleConfig<ClipboardHistoryConfig>("ClipboardHistory");
            Assert.IsNotNull(reloaded);
            Assert.IsTrue(reloaded.Enabled);
            Assert.IsFalse(reloaded.AutoRecord);
            Assert.AreEqual("Ctrl+Shift+V", reloaded.Hotkey);
            Assert.AreEqual(80, reloaded.MaxPreviewChars);
            Assert.AreEqual(500, reloaded.MaxItems);
            Assert.AreEqual(60, reloaded.RetentionDays);
        }

        [TestMethod]
        public void ClipboardHistoryWindow_CanBeInstantiatedAndShown()
        {
            Exception error = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var storage = new MemoryClipboardStorage();
                    var service = new ClipboardHistoryService(storage);
                    service.Start(new ClipboardHistoryConfig());
                    var win = new CarroDesk.Modules.ClipboardHistory.Views.ClipboardHistoryWindow(service);
                    win.ShowAndActivate();
                    win.Close();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (error != null)
            {
                Assert.Fail("Window creation/show failed: " + error);
            }
        }
    }
}
