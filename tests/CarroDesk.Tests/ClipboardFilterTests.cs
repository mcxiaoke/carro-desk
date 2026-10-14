using System;
using System.Collections.Generic;
using System.Linq;
using CarroDesk.Modules.ClipboardHistory;
using CarroDesk.Modules.ClipboardHistory.Models;
using CarroDesk.Modules.ClipboardHistory.Services;
using CarroDesk.Modules.ClipboardHistory.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 剪贴板敏感信息过滤测试。
    ///
    /// 约定（与设计一致，改动前务必确认用户是否仍然要这个语义）：
    ///   · 命中即整条丢弃：不入内存、不落盘；
    ///   · 只影响此后的记录，不回溯删除历史中已存在的同内容条目；
    ///   · 匹配发生在 MaxTextLength 截断之前，关键字落在被截掉的尾部同样会拦截；
    ///   · 总开关默认关闭。
    /// </summary>
    [TestClass]
    public class ClipboardFilterTests
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

        private static ClipboardHistoryConfig ConfigWith(params string[] patterns)
        {
            var config = new ClipboardHistoryConfig { FilterEnabled = true };
            foreach (var pattern in patterns)
            {
                config.FilterRules.Add(new ClipboardFilterRule { Pattern = pattern });
            }
            return config;
        }

        [TestMethod]
        public void ClipboardFilterConfig_Defaults_AreDisabled()
        {
            var config = ClipboardHistoryConfig.CreateDefault();
            Assert.IsFalse(config.FilterEnabled, "敏感过滤默认应关闭，不得改变既有用户的记录行为");
            Assert.AreEqual(0, config.FilterRules.Count);
        }

        [TestMethod]
        public void ClipboardFilter_KeywordHit_IsNotRecorded()
        {
            var storage = new MemoryClipboardStorage();
            var service = new ClipboardHistoryService(storage);
            service.Start(ConfigWith("password"));

            service.RecordText("user=root password=hunter2");

            Assert.AreEqual(0, service.Count, "命中关键字的内容不得进入历史");
            Assert.AreEqual(0, storage.SavedItems.Count, "命中内容不得落盘");
        }

        [TestMethod]
        public void ClipboardFilter_WithoutHit_RecordsNormally()
        {
            var service = new ClipboardHistoryService(new MemoryClipboardStorage());
            service.Start(ConfigWith("password"));

            service.RecordText("普通的一段文本");

            Assert.AreEqual(1, service.Count);
            Assert.AreEqual("普通的一段文本", service.GetItems()[0].FullText);
        }

        [TestMethod]
        public void ClipboardFilter_IgnoreCaseFlag_IsHonored()
        {
            var config = ConfigWith("Secret");
            config.FilterRules[0].IgnoreCase = true;
            var service = new ClipboardHistoryService(new MemoryClipboardStorage());
            service.Start(config);
            service.RecordText("my SECRET value");
            Assert.AreEqual(0, service.Count, "忽略大小写时应命中");

            var strict = ConfigWith("Secret");
            strict.FilterRules[0].IgnoreCase = false;
            var service2 = new ClipboardHistoryService(new MemoryClipboardStorage());
            service2.Start(strict);
            service2.RecordText("my SECRET value");
            Assert.AreEqual(1, service2.Count, "区分大小写时大小写不同不应命中");
        }

        [TestMethod]
        public void ClipboardFilter_DisabledRule_DoesNotBlock()
        {
            var config = ConfigWith("password");
            config.FilterRules[0].Enabled = false;
            var service = new ClipboardHistoryService(new MemoryClipboardStorage());
            service.Start(config);

            service.RecordText("password=hunter2");

            Assert.AreEqual(1, service.Count, "被停用的单条规则不应拦截");
        }

        [TestMethod]
        public void ClipboardFilter_TotalSwitchOff_DoesNotBlock()
        {
            var config = ConfigWith("password");
            config.FilterEnabled = false;
            var service = new ClipboardHistoryService(new MemoryClipboardStorage());
            service.Start(config);

            service.RecordText("password=hunter2");

            Assert.AreEqual(1, service.Count, "总开关关闭时不应拦截");
        }

        [TestMethod]
        public void ClipboardFilter_ApplyConfigOff_StopsFilteringImmediately()
        {
            var storage = new MemoryClipboardStorage();
            var service = new ClipboardHistoryService(storage);
            service.Start(ConfigWith("password"));

            var off = ConfigWith("password");
            off.FilterEnabled = false;
            service.ApplyConfig(off);
            service.RecordText("password=hunter2");
            Assert.AreEqual(1, service.Count, "配置热更新后应立即停止过滤");

            service.ApplyConfig(ConfigWith("password"));
            service.RecordText("password=another");
            Assert.AreEqual(1, service.Count, "重新开启过滤后应恢复拦截");
        }

        [TestMethod]
        public void ClipboardFilter_Hit_DoesNotRemovePreviouslyStoredItems()
        {
            var storage = new MemoryClipboardStorage();
            var service = new ClipboardHistoryService(storage);

            // 先在无过滤状态下记录一条
            var off = ConfigWith("password");
            off.FilterEnabled = false;
            service.Start(off);
            service.RecordText("password=old");

            // 再开启过滤后复制同样的内容：本次不入库，但历史中的旧条目保持原样
            service.ApplyConfig(ConfigWith("password"));
            service.RecordText("password=old");

            Assert.AreEqual(1, service.Count, "按既定语义，过滤只影响此后的记录，不回溯删除旧条目");
            Assert.AreEqual("password=old", service.GetItems()[0].FullText);
        }

        [TestMethod]
        public void ClipboardFilter_MatchesBeforeMaxTextLengthTruncation()
        {
            var config = ConfigWith("TAILKEY");
            config.MaxTextLength = 50;
            var service = new ClipboardHistoryService(new MemoryClipboardStorage());
            service.Start(config);

            // 关键字位于 50 字符截断点之后：必须在截断前匹配，否则会漏检
            service.RecordText(new string('a', 100) + "TAILKEY");

            Assert.AreEqual(0, service.Count, "关键字落在截断范围之外时同样应被拦截");
        }

        [TestMethod]
        public void ClipboardFilter_Hit_RaisesContentFilteredWithoutRawText()
        {
            var service = new ClipboardHistoryService(new MemoryClipboardStorage());
            service.Start(ConfigWith("password"));

            ClipboardFilterRule hitRule = null;
            int hitLength = 0;
            service.ContentFiltered += (rule, length) =>
            {
                hitRule = rule;
                hitLength = length;
            };

            service.RecordText("password=hunter2");

            Assert.IsNotNull(hitRule, "命中必须派发事件以便留痕");
            Assert.AreEqual("password", hitRule.Pattern);
            Assert.AreEqual("password=hunter2".Length, hitLength, "事件只应携带字符数，不得携带原文");
        }

        [TestMethod]
        public void ClipboardFilter_NoHit_DoesNotRaiseEvent()
        {
            var service = new ClipboardHistoryService(new MemoryClipboardStorage());
            service.Start(ConfigWith("password"));

            int raised = 0;
            service.ContentFiltered += (rule, length) => raised++;

            service.RecordText("完全正常的文本");

            Assert.AreEqual(0, raised);
        }

        [TestMethod]
        public void ClipboardTextFilter_IgnoresNullAndBlankRules()
        {
            var rules = new List<ClipboardFilterRule>
            {
                null,
                new ClipboardFilterRule { Pattern = "   " },
                new ClipboardFilterRule { Pattern = "ok", Enabled = true }
            };

            var filter = new ClipboardTextFilter(rules, true);

            Assert.AreEqual(1, filter.RuleCount, "空规则与停用规则不应进入匹配集");
            Assert.IsTrue(filter.IsActive);

            ClipboardFilterRule hit;
            Assert.IsTrue(filter.TryMatch("xx ok xx", out hit));
            Assert.AreEqual("ok", hit.Pattern);

            Assert.IsFalse(filter.TryMatch("nothing here", out hit));
        }

        [TestMethod]
        public void ClipboardTextFilter_EmptyFilter_IsInactiveAndNeverMatches()
        {
            var disabled = new ClipboardTextFilter(
                new List<ClipboardFilterRule> { new ClipboardFilterRule { Pattern = "x" } }, false);

            Assert.IsFalse(disabled.IsActive);

            ClipboardFilterRule hit;
            Assert.IsFalse(disabled.TryMatch("xxx", out hit));
            Assert.IsNull(hit);
            Assert.IsFalse(ClipboardTextFilter.Empty.IsActive);
            Assert.IsFalse(ClipboardTextFilter.FromConfig(null).IsActive);
        }

        [TestMethod]
        public void ClipboardHistoryConfig_Clone_DeepCopiesFilterRules()
        {
            var config = ConfigWith("password");
            var clone = config.Clone();

            clone.FilterRules[0].Pattern = "changed";
            clone.FilterRules.Add(new ClipboardFilterRule { Pattern = "extra" });

            Assert.AreEqual("password", config.FilterRules[0].Pattern, "Clone 必须深拷贝规则列表");
            Assert.AreEqual(1, config.FilterRules.Count);
        }

        [TestMethod]
        public void ClipboardFilterConfig_JsonRoundTrip_KeepsRules()
        {
            var config = ConfigWith("password", "AKIA");
            config.FilterRules[1].IgnoreCase = false;

            var restored = JsonConvert.DeserializeObject<ClipboardHistoryConfig>(JsonConvert.SerializeObject(config));

            Assert.IsTrue(restored.FilterEnabled);
            Assert.AreEqual(2, restored.FilterRules.Count);
            Assert.AreEqual("password", restored.FilterRules[0].Pattern);
            Assert.IsTrue(restored.FilterRules[0].IgnoreCase);
            Assert.IsFalse(restored.FilterRules[1].IgnoreCase);
            Assert.AreEqual("AKIA", restored.FilterRules[1].Pattern);
        }

        [TestMethod]
        public void ClipboardHistoryModule_TrayMenu_ExposesFilterEntries()
        {
            var module = new ClipboardHistoryModule();
            var root = module.GetTrayMenuItems()?.First();
            Assert.IsNotNull(root);

            var toggle = root.Children.FirstOrDefault(x => x.Id == "clipboard_filter_toggle");
            var settings = root.Children.FirstOrDefault(x => x.Id == "clipboard_filter_settings");
            Assert.IsNotNull(toggle, "托盘应提供敏感过滤快速开关");
            Assert.IsNotNull(settings, "托盘应提供敏感过滤设置入口");
            Assert.IsFalse(toggle.IsChecked, "默认关闭时托盘开关应为未勾选");
        }

        [TestMethod]
        public void ClipboardFilterSettingsWindow_CanBeInstantiatedAndShown()
        {
            TestEnvironment.RunInSta(() =>
            {
                var win = new ClipboardFilterSettingsWindow();
                win.Show();
                win.Close();
            });
        }
    }
}
