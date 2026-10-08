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
        public void TrayContextMenu_TitleIncludesVersion_AndHasAboutItem_WithoutLanguageMenu()
        {
            TestEnvironment.RunInSta(() =>
            {
                var menu = new TrayContextMenu();

                // 验证顶部标题与版本号
                Assert.IsNotNull(menu.TitleVersionText, "TitleVersionText 控件应存在");
                Assert.AreEqual(AppInfo.DisplayVersion, menu.TitleVersionText.Text, "TitleVersionText 应展示 AppInfo.DisplayVersion");

                // 验证关于菜单项存在，且不再包含语言子菜单
                bool foundAbout = false;
                bool foundLanguage = false;

                foreach (var item in menu.Items)
                {
                    if (item is MenuItem mi)
                    {
                        if (mi.Name == "LanguageMenu")
                        {
                            foundLanguage = true;
                        }

                        string headerText = mi.Header?.ToString() ?? "";
                        if (headerText.Contains("关于") || headerText.Contains("About"))
                        {
                            foundAbout = true;
                        }
                    }
                }

                Assert.IsTrue(foundAbout, "托盘菜单应包含【关于】菜单项");
                Assert.IsFalse(foundLanguage, "托盘菜单应已移除【语言】菜单项");
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
