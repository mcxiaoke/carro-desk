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
            Assert.IsTrue(root.Children.Any(c => c.Id == "appautomute_unmute_all"), "二级菜单应包含应急恢复所有声音项");
            Assert.IsTrue(root.Children.Any(c => c.Id == "appautomute_add_current"), "二级菜单应包含将当前应用加入列表项");
        }

        [TestMethod]
        public void AppAutoMuteModule_UnmuteAllTargets_SafeWhenUninitialized()
        {
            var module = new AppAutoMuteModule();
            // 未启动或未初始化服务时调用不应抛异常
            module.UnmuteAllTargets();
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

        [TestMethod]
        public void AppAutoMuteModule_OnStartFailsGracefully_AndBlacklistSwitchStartsMuteTimer()
        {
            var module = new AppAutoMuteModule();
            var container = new CarroDesk.Host.Services.ServiceContainer();
            var manager = new CarroDesk.Host.Services.ModuleManager();
            manager.RegisterModule(module);
            manager.InitializeAll(container);
            manager.StartAll();
            Assert.AreEqual(CarroDesk.Core.ModuleStatus.Faulted, module.Status,
                "缺少依赖时 Start 应安全回滚到 Faulted 而不触发 OnStop NRE");

            // 黑名单双目标直切应启动 _muteTimer
            var mod2 = new AppAutoMuteModule();
            mod2.Config.TargetApps = new List<string> { "chrome.exe", "vlc.exe" };
            var eval = typeof(AppAutoMuteModule).GetMethod("EvaluateForeground",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var muteTimerField = typeof(AppAutoMuteModule).GetField("_muteTimer",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            eval.Invoke(mod2, new object[] { "vlc.exe" });
            var muteTimer = (System.Windows.Threading.DispatcherTimer)muteTimerField.GetValue(mod2);
            Assert.IsTrue(muteTimer.IsEnabled, "黑名单切到另一个目标应用时也应启动 _muteTimer 静音后台旧目标");
            muteTimer.Stop();
        }
    }
}
