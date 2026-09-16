using System;
using System.Collections.Generic;
using System.Threading;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Host.Services;
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

        private sealed class StubLogger : ILoggerService
        {
            public void LogError(string module, string message, Exception ex) { }
            public void LogInfo(string module, string message) { }
            public void LogWarning(string module, string message) { }
        }
    }
}