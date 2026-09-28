using System;
using System.Linq;
using System.Windows.Threading;
using CarroDesk.Core;
using CarroDesk.Core.Commands;
using CarroDesk.Host.Commands;
using CarroDesk.Host.Services;
using CarroDesk.Modules.Awake;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace CarroDesk.Tests
{
    /// <summary>
    /// awake.* 能力验收（IPC 设计 §4.2）：status/on/off 三能力、minutes 范围校验、
    /// 定时/永久两态。通过 ModuleManager 集成（Dispatcher 用当前测试线程，
    /// 与既有 AwakeService 测试同款模式；Timer 无 Run 循环不会触发 Tick）。
    /// </summary>
    [TestClass]
    public class AwakeCapabilityTests
    {
        private static AwakeModule CreateRunningModule()
        {
            var manager = new ModuleManager { Dispatcher = Dispatcher.CurrentDispatcher };
            var module = new AwakeModule();
            manager.RegisterModule(module);
            manager.InitializeAll(new ServiceContainer());
            manager.StartAll();
            return module;
        }

        private static CommandHost BuildHost(AwakeModule module)
        {
            var registry = new CommandRegistry();
            foreach (var command in module.GetCommands())
            {
                registry.Register(module.Id, command);
            }
            return new CommandHost(
                registry,
                id => ModuleStatus.Running,
                null,
                new NullCommandAuditSink());
        }

        private static JObject AsJObject(CommandResult result)
        {
            Assert.IsTrue(result.Ok, result.Error != null ? result.Error.Message : "");
            return (JObject)JToken.FromObject(result.Data);
        }

        [TestMethod]
        public void GetCommands_DescriptorsAndRisks()
        {
            var module = CreateRunningModule();
            var commands = module.GetCommands().ToList();

            CollectionAssert.AreEquivalent(
                new[] { "awake.status", "awake.on", "awake.off" },
                commands.Select(c => c.Name).ToList());
            Assert.AreEqual(CommandRisk.ReadOnly, commands.First(c => c.Name == "awake.status").Risk);
            Assert.AreEqual(CommandRisk.Low, commands.First(c => c.Name == "awake.on").Risk);
            Assert.AreEqual(CommandRisk.Low, commands.First(c => c.Name == "awake.off").Risk);
            Assert.IsTrue(commands.All(c => !c.RequiresPin));
        }

        [TestMethod]
        public void Invoke_On_WithoutMinutes_IsIndefinite()
        {
            var module = CreateRunningModule();
            var host = BuildHost(module);

            var status = AsJObject(host.Invoke(CommandRequest.Create("awake.on")));
            Assert.AreEqual("indefinite", (string)status["mode"]);
            Assert.AreEqual(true, (bool)status["isActive"]);
            // 定时相关的字段在非定时模式下序列化为 JSON null
            Assert.AreEqual(JTokenType.Null, status["remainingMinutes"].Type);
            Assert.AreEqual(JTokenType.Null, status["expireAt"].Type);
        }

        [TestMethod]
        public void Invoke_On_WithMinutes_IsTimed()
        {
            var module = CreateRunningModule();
            var host = BuildHost(module);

            var request = CommandRequest.Create("awake.on");
            request.Params["minutes"] = 120;
            var status = AsJObject(host.Invoke(request));

            Assert.AreEqual("timed", (string)status["mode"]);
            Assert.AreEqual(true, (bool)status["isActive"]);
            Assert.AreEqual(120, (int)status["remainingMinutes"]);
            Assert.IsNotNull(status["expireAt"]);
        }

        [TestMethod]
        public void Invoke_On_StringMinutes_CoercedByKernel()
        {
            var module = CreateRunningModule();
            var host = BuildHost(module);

            var request = CommandRequest.Create("awake.on");
            request.Params["minutes"] = "45";
            var status = AsJObject(host.Invoke(request));

            Assert.AreEqual("timed", (string)status["mode"]);
            Assert.AreEqual(45, (int)status["remainingMinutes"]);
        }

        [TestMethod]
        public void Invoke_On_OutOfRangeMinutes_Rejected()
        {
            var module = CreateRunningModule();
            var host = BuildHost(module);

            foreach (var bad in new[] { 0, -5, 1441 })
            {
                var request = CommandRequest.Create("awake.on");
                request.Params["minutes"] = bad;
                var result = host.Invoke(request);
                Assert.IsFalse(result.Ok);
                Assert.AreEqual(CommandErrorCodes.InvalidParams, result.Error.Code, "minutes=" + bad);
            }
        }

        [TestMethod]
        public void Invoke_Off_ReturnsToPassive()
        {
            var module = CreateRunningModule();
            var host = BuildHost(module);

            Assert.IsTrue(host.Invoke(CommandRequest.Create("awake.on")).Ok);
            var status = AsJObject(host.Invoke(CommandRequest.Create("awake.off")));

            Assert.AreEqual("passive", (string)status["mode"]);
            // passive 且无进程联动时 isActive=false
            Assert.AreEqual(false, (bool)status["isActive"]);
        }

        [TestMethod]
        public void Invoke_Status_ReflectsState()
        {
            var module = CreateRunningModule();
            var host = BuildHost(module);

            var status = AsJObject(host.Invoke(CommandRequest.Create("awake.status")));
            Assert.IsNotNull(status["mode"]);
            Assert.IsNotNull(status["isActive"]);
            Assert.IsNotNull(status["keepDisplayOn"]);
        }
    }
}
