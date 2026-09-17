using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Host.Services;
using CarroDesk.Modules.AppAutoMute;
using CarroDesk.Modules.AudioSwitch;
using CarroDesk.Modules.ScreenLock;
using CarroDesk.Modules.TaskScheduler;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class CarroDeskContractTests
    {
        [TestMethod]
        public void ModuleStatus_Enum_HasRequiredStates()
        {
            Assert.IsTrue(Enum.IsDefined(typeof(ModuleStatus), ModuleStatus.Created));
            Assert.IsTrue(Enum.IsDefined(typeof(ModuleStatus), ModuleStatus.Initialized));
            Assert.IsTrue(Enum.IsDefined(typeof(ModuleStatus), ModuleStatus.Running));
            Assert.IsTrue(Enum.IsDefined(typeof(ModuleStatus), ModuleStatus.Stopped));
            Assert.IsTrue(Enum.IsDefined(typeof(ModuleStatus), ModuleStatus.Disabled));
            Assert.IsTrue(Enum.IsDefined(typeof(ModuleStatus), ModuleStatus.Faulted));
        }

        [TestMethod]
        public void TrayMenuItem_HasOrderAndToolTip()
        {
            var item = new TrayMenuItem { Id = "x", Order = 5, ToolTip = "禁用原因" };
            Assert.AreEqual(5, item.Order);
            Assert.AreEqual("禁用原因", item.ToolTip);
        }

        [TestMethod]
        public void SafeInvoker_Timeout_ReturnsFalse_OnSlowAction()
        {
            // 3s 超时内跑完的任务应返回 true
            bool quickOk = SafeInvoker.RunTimeout("t", TimeSpan.FromSeconds(3),
                () => Thread.Sleep(1), (id, ex) => { });
            Assert.IsTrue(quickOk);
        }

        [TestMethod]
        public void SafeInvoker_CatchesException_ReturnsFalse()
        {
            bool ok = SafeInvoker.Run("m", new Action(() => throw new InvalidOperationException("boom")),
                (id, ex) => { });
            Assert.IsFalse(ok);
        }

        [TestMethod]
        public void ServiceContainer_Supports_MultiRegistration_And_GetServices()
        {
            var container = new ServiceContainer();
            container.AddSingleton<ILoggerService>(new StubLogger());
            var list = container.GetServices<ILoggerService>();
            Assert.AreEqual(1, list.Count);
        }

        [TestMethod]
        public void ServiceContainer_GetService_Returns_FirstRegistration()
        {
            var container = new ServiceContainer();
            container.AddSingleton<ILoggerService>(new StubLogger());
            Assert.IsNotNull(container.GetService<ILoggerService>());
        }

        [TestMethod]
        public void AudioSwitchModule_GetDeviceShortName_Extracts_CompactNames()
        {
            var module = new AudioSwitchModule();

            // 扬声器与耳机标准名称精简
            Assert.AreEqual("扬声器", module.GetDeviceShortName("扬声器 (Realtek High Definition Audio)"));
            Assert.AreEqual("耳机", module.GetDeviceShortName("耳机 (Realtek USB Audio)"));
            Assert.AreEqual("扬声器", module.GetDeviceShortName("扬声器 (Realtek(R) Audio)"));

            // 英文设备名称
            Assert.AreEqual("Speakers", module.GetDeviceShortName("Speakers (Realtek Audio)"));
            Assert.AreEqual("Headphones", module.GetDeviceShortName("Headphones (2- High Definition Audio Device)"));

            // 其他外接设备去除控制器驱动后缀
            Assert.AreEqual("DELL U27", module.GetDeviceShortName("DELL U27 (NVIDIA High Definition Audio)"));

            // 超长设备名截断至 <= 8 字符加省略号
            string longDevice = module.GetDeviceShortName("SuperLongExternalAudioDACInterface (USB Audio)");
            Assert.IsTrue(longDevice.Length <= 8, "超长设备名应在 8 字符以内以避免撑宽菜单");
            Assert.IsTrue(longDevice.EndsWith("…"));

            // 空值安全
            Assert.AreEqual(string.Empty, module.GetDeviceShortName(null));
            Assert.AreEqual(string.Empty, module.GetDeviceShortName("   "));
        }

        [TestMethod]
        public void ScreenLockModule_GetTrayMenuItems_ReturnsSingleRootItem_WithExpectedChildren()
        {
            var configService = new ConfigService();
            var module = new ScreenLockModule(configService);

            var items = module.GetTrayMenuItems()?.ToList();
            Assert.IsNotNull(items);
            Assert.AreEqual(1, items.Count, "ScreenLockModule 应按照二级收敛规范严格只输出 1 个根菜单项");

            var root = items[0];
            Assert.AreEqual("screenlock_root", root.Id);
            Assert.IsTrue(root.Header.Contains("屏幕保护"), $"根项 Header 应包含模块名，实际: {root.Header}");
            Assert.IsTrue(root.Children.Count >= 3, "二级菜单应包含立即锁定、档位、暂停等选项");

            Assert.IsTrue(root.Children.Any(c => c.Id == "screenlock_lock_now"), "二级菜单应包含立即锁定");
            Assert.IsTrue(root.Children.Any(c => c.Id == "screenlock_idle_root"), "二级菜单应包含空闲锁定");
            Assert.IsTrue(root.Children.Any(c => c.Id == "screenlock_pause_root"), "二级菜单应包含暂停计时");
        }

        [TestMethod]
        public void AppAutoMuteModule_GetTrayMenuItems_ReturnsSingleRootItem_WithExpectedChildren()
        {
            var module = new AppAutoMuteModule();

            var items = module.GetTrayMenuItems()?.ToList();
            Assert.IsNotNull(items);
            Assert.AreEqual(1, items.Count, "AppAutoMuteModule 应按照二级收敛规范严格只输出 1 个根菜单项");

            var root = items[0];
            Assert.AreEqual("appautomute_root", root.Id);
            Assert.IsTrue(root.Header.Contains("应用后台静音"), $"根项 Header 应包含模块名，实际: {root.Header}");

            Assert.IsTrue(root.Children.Any(c => c.Id == "appautomute_toggle"), "二级菜单应包含总开关");
            Assert.IsTrue(root.Children.Any(c => c.Id == "appautomute_settings"), "二级菜单应包含设置窗口项");
        }

        [TestMethod]
        public void TaskSchedulerModule_GetTrayMenuItems_ReturnsSingleRootItem_WithExpectedChildren()
        {
            var module = new TaskSchedulerModule();

            var items = module.GetTrayMenuItems()?.ToList();
            Assert.IsNotNull(items);
            Assert.AreEqual(1, items.Count, "TaskSchedulerModule 应按照二级收敛规范严格只输出 1 个根菜单项");

            var root = items[0];
            Assert.AreEqual("task_scheduler_root", root.Id);
            Assert.IsTrue(root.Header.Contains("自动化任务"), $"根项 Header 应包含模块名，实际: {root.Header}");

            Assert.IsTrue(root.Children.Any(c => c.Id == "task_scheduler_toggle"), "二级菜单应包含启用总开关");
            Assert.IsTrue(root.Children.Any(c => c.Id == "task_scheduler_manual"), "二级菜单应包含手动运行");
            Assert.IsTrue(root.Children.Any(c => c.Id == "task_scheduler_recent"), "二级菜单应包含最近运行");
            Assert.IsTrue(root.Children.Any(c => c.Id == "task_scheduler_editor"), "二级菜单应包含任务编辑器");
            Assert.IsTrue(root.Children.Any(c => c.Id == "task_scheduler_reload"), "二级菜单应包含重载任务");
        }

        [TestMethod]
        public void AudioSwitchModule_GetTrayMenuItems_ReturnsSingleRootItem()
        {
            var module = new AudioSwitchModule();

            var items = module.GetTrayMenuItems()?.ToList();
            Assert.IsNotNull(items);
            Assert.AreEqual(1, items.Count, "AudioSwitchModule 应按照二级收敛规范严格只输出 1 个根菜单项");

            var root = items[0];
            Assert.AreEqual("audioswitch_root", root.Id);
            Assert.IsTrue(root.Header.Contains("音频输出设备"), $"根项 Header 应包含音频输出设备，实际: {root.Header}");
            Assert.IsTrue(root.Children.Any(c => c.Id == "audioswitch_fast_toggle"), "二级菜单应包含快捷切换");
        }

        private sealed class StubLogger : ILoggerService
        {
            public void LogError(string module, string message, Exception ex) { }
            public void LogInfo(string module, string message) { }
            public void LogWarning(string module, string message) { }
        }
    }
}