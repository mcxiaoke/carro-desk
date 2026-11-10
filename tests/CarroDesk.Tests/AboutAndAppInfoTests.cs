using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using CarroDesk.Common;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Host.Services;
using CarroDesk.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class AboutAndAppInfoTests
    {
        private class DummyModule : IModule
        {
            public string Id { get; set; } = "dummy_module";
            public string Name { get; set; } = "虚拟测试模块";
            public string Description { get; set; } = "用于测试的虚拟模块";
            public string Version { get; set; } = "1.0.0";
            public int Order { get; set; } = 1;
            public bool DefaultEnabled => true;
            public bool IsRunning => Status == ModuleStatus.Running;
            public ModuleStatus Status { get; set; } = ModuleStatus.Running;

            public void Initialize(IModuleContext context) { }
            public void Start() { }
            public void Stop() { }
            public void MarkFaulted() { }
            public void OnConfigReloaded() { }
            public void OnLanguageChanged() { }
            public IEnumerable<TrayMenuItem> GetTrayMenuItems() => Enumerable.Empty<TrayMenuItem>();
            public void Dispose() { }
        }

        [TestMethod]
        public void AppInfo_Properties_AreValidAndNotNull()
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(AppInfo.ProductName), "ProductName 不应为空");
            Assert.IsFalse(string.IsNullOrWhiteSpace(AppInfo.Version), "Version 不应为空");
            Assert.IsFalse(string.IsNullOrWhiteSpace(AppInfo.DisplayVersion), "DisplayVersion 不应为空");
            Assert.IsTrue(AppInfo.DisplayVersion.StartsWith("v"), "DisplayVersion 应以 'v' 开头: " + AppInfo.DisplayVersion);
            Assert.IsFalse(string.IsNullOrWhiteSpace(AppInfo.DotNetRuntime), "DotNetRuntime 不应为空");
            Assert.IsFalse(string.IsNullOrWhiteSpace(AppInfo.OperatingSystem), "OperatingSystem 不应为空");
            Assert.IsFalse(string.IsNullOrWhiteSpace(AppInfo.DataPath), "DataPath 不应为空");
        }

        [TestMethod]
        public void AppInfo_GetDiagnosticReport_IncludesModuleInfo()
        {
            var dummy = new DummyModule();
            string report = AppInfo.GetDiagnosticReport(new[] { dummy });

            StringAssert.Contains(report, "CarroDesk");
            StringAssert.Contains(report, AppInfo.DisplayVersion);
            StringAssert.Contains(report, "虚拟测试模块");
            StringAssert.Contains(report, "dummy_module");
            StringAssert.Contains(report, "Running");
        }

        [TestMethod]
        public void TrayContextMenu_TitleIncludesVersion_AndHasSettingsEntry_WithoutScatteredItems()
        {
            TestEnvironment.RunInSta(() =>
            {
                var menu = new TrayContextMenu();

                // 验证顶部标题与版本号
                Assert.IsNotNull(menu.TitleVersionText, "TitleVersionText 控件应存在");
                Assert.AreEqual(AppInfo.DisplayVersion, menu.TitleVersionText.Text, "TitleVersionText 应展示 AppInfo.DisplayVersion");

                // 设置入口存在
                Assert.IsNotNull(menu.SettingsItem, "托盘菜单应包含【设置】入口");
                string settingsHeader = menu.SettingsItem.Header?.ToString() ?? "";
                Assert.IsTrue(settingsHeader.Contains("设置") || settingsHeader.Contains("Settings"),
                    "设置入口文案应本地化，实际: " + settingsHeader);

                // 关于/诊断/语言/开机自启已收敛到设置窗口，不再散落在托盘顶层
                bool hasAbout = false;
                bool hasDiagnostics = false;
                bool hasLanguage = false;
                bool hasAutoStart = false;

                foreach (var item in menu.Items.OfType<MenuItem>())
                {
                    if (item == menu.SettingsItem) continue;

                    string headerText = item.Header?.ToString() ?? "";
                    if (headerText.Contains("关于") || headerText.Contains("About")) hasAbout = true;
                    if (headerText.Contains("诊断") || headerText.Contains("Diagnostic")) hasDiagnostics = true;
                    if (headerText.Contains("语言") || headerText.Contains("Language")) hasLanguage = true;
                    if (headerText.Contains("开机自启") || headerText.Contains("Start with Windows")) hasAutoStart = true;
                }

                Assert.IsFalse(hasAbout, "关于应移入设置窗口");
                Assert.IsFalse(hasDiagnostics, "诊断信息应移入设置窗口");
                Assert.IsFalse(hasLanguage, "语言切换应移入设置窗口");
                Assert.IsFalse(hasAutoStart, "开机自启应移入设置窗口");
            });
        }

        [TestMethod]
        public void AboutWindow_InitializesCorrectly_InSta()
        {
            TestEnvironment.RunInSta(() =>
            {
                var modules = new ModuleManager();
                modules.RegisterModule(new DummyModule());

                var win = new AboutWindow(modules);
                Assert.IsNotNull(win, "AboutWindow 应当成功构造");
                Assert.AreEqual(AppInfo.DisplayVersion, win.VersionBadgeText.Text);
                Assert.IsNotNull(win.ModulesItemsControl.ItemsSource, "模块列表数据源不应为空");

                win.Close();
            });
        }
    }
}
