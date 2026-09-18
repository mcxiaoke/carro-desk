using System;
using System.Collections.Generic;
using System.Linq;
using CarroDesk.Modules.AppAutoMute;
using CarroDesk.Modules.AppAutoMute.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class AppAutoMuteModuleTests
    {
        [TestMethod]
        public void App_IsFatalException_IdentifiesCriticalErrors()
        {
            Assert.IsTrue(App.IsFatalException(new OutOfMemoryException()));
            Assert.IsTrue(App.IsFatalException(new StackOverflowException()));
            Assert.IsTrue(App.IsFatalException(new AccessViolationException()));

            // 常规异常不应判定为 Fatal
            Assert.IsFalse(App.IsFatalException(new InvalidOperationException("normal error")));
            Assert.IsFalse(App.IsFatalException(new ArgumentNullException("param")));
            Assert.IsFalse(App.IsFatalException(null));
        }

        [TestMethod]
        public void AppAutoMuteModule_MenuContract_FollowsStandard()
        {
            var module = new AppAutoMuteModule();
            Assert.AreEqual("AppAutoMute", module.Id);
            Assert.IsTrue(module.DefaultEnabled);

            var items = module.GetTrayMenuItems().ToList();
            Assert.AreEqual(1, items.Count, "托盘菜单必须使用单一根节点");
            var root = items[0];
            Assert.AreEqual("appautomute_root", root.Id);
            Assert.IsTrue(root.Children.Count >= 3);
        }

        [TestMethod]
        public void AppAutoMuteConfig_Defaults_Valid()
        {
            var cfg = new AppAutoMuteConfig();
            Assert.IsTrue(cfg.Enabled);
            Assert.AreEqual("Blacklist", cfg.Mode);
            Assert.IsNotNull(cfg.TargetApps);
            Assert.AreEqual(1000, cfg.MuteDelayMs);
            Assert.AreEqual(500, cfg.UnmuteDelayMs);
        }
    }
}
