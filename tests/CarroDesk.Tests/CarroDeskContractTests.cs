using System;
using System.Collections.Generic;
using System.Threading;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Host.Services;
using CarroDesk.Modules.AudioSwitch;
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

        private sealed class StubLogger : ILoggerService
        {
            public void LogError(string module, string message, Exception ex) { }
            public void LogInfo(string module, string message) { }
            public void LogWarning(string module, string message) { }
        }
    }
}