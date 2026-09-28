using System;
using System.Linq;
using CarroDesk.Core;
using CarroDesk.Core.Commands;
using CarroDesk.Host.Commands;
using CarroDesk.Host.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace CarroDesk.Tests
{
    /// <summary>
    /// host.* 能力验收：host 伪模块（实测发现 1 修复）、每模块 capabilityCount
    /// （发现 2 的可见性）、host.guide 嵌入手册。
    /// </summary>
    [TestClass]
    public class HostCommandsTests
    {
        private sealed class SilentModule : ModuleBase<DummyConfig>
        {
            private readonly string _id;
            public SilentModule(string id) { _id = id; }
            public override string Id { get { return _id; } }
            public override string Name { get { return _id; } }
            protected override void OnStart() { }
            protected override void OnStop() { }
        }

        private sealed class DummyConfig { }

        private static CommandHost BuildHost(ModuleManager modules, out CommandRegistry registry)
        {
            registry = new CommandRegistry();
            if (modules != null)
            {
                foreach (var pair in modules.CollectCommands())
                {
                    registry.Register(pair.Key, pair.Value);
                }
            }
            HostCommands.Install(registry, modules);
            return new CommandHost(
                registry,
                id => string.Equals(id, HostCommands.ModuleId, StringComparison.OrdinalIgnoreCase)
                    ? ModuleStatus.Running
                    : (modules != null ? modules.GetModuleStatus(id) : ModuleStatus.Created),
                null,
                new NullCommandAuditSink());
        }

        [TestMethod]
        public void ModulesList_HostPseudoModuleFirst_WithCapabilityCounts()
        {
            var manager = new ModuleManager();
            manager.RegisterModule(new SilentModule("Silent")); // 无能力模块
            var services = new ServiceContainer();
            manager.InitializeAll(services);
            manager.StartAll();

            CommandRegistry registry;
            var host = BuildHost(manager, out registry);

            var result = host.Invoke(CommandRequest.Create("host.modules.list"));
            Assert.IsTrue(result.Ok, result.Error != null ? result.Error.Message : "");
            var modules = (JArray)JToken.FromObject(result.Data);

            // 首项 = host 伪模块（实测发现 1 的修复）
            var first = modules[0];
            Assert.AreEqual("host", (string)first["id"]);
            Assert.AreEqual(true, (bool)first["isHost"]);
            Assert.AreEqual(true, (bool)first["isRunning"]);

            // host.* 共 5 个能力（hello/guide/status/modules.list/capabilities.list）
            Assert.AreEqual(5, (int)first["capabilityCount"]);

            // 无能力模块可见：capabilityCount=0（发现 2 的可见性）
            var silent = modules.First(m => (string)m["id"] == "Silent");
            Assert.AreEqual(0, (int)silent["capabilityCount"]);
            Assert.AreEqual(false, (bool)silent["isHost"]);
        }

        [TestMethod]
        public void Status_IncludesHostPseudoModule()
        {
            var manager = new ModuleManager();
            manager.RegisterModule(new SilentModule("Silent"));
            var services = new ServiceContainer();
            manager.InitializeAll(services);
            manager.StartAll();

            CommandRegistry registry;
            var host = BuildHost(manager, out registry);

            var result = host.Invoke(CommandRequest.Create("host.status"));
            Assert.IsTrue(result.Ok, result.Error != null ? result.Error.Message : "");
            var payload = JToken.FromObject(result.Data);
            Assert.AreEqual("host", (string)payload["modules"][0]["id"]);
            Assert.AreEqual(true, (bool)payload["modules"][0]["isHost"]);
        }

        [TestMethod]
        public void Guide_ReturnsEmbeddedManual()
        {
            var manager = new ModuleManager();
            var services = new ServiceContainer();
            manager.InitializeAll(services);
            manager.StartAll();

            CommandRegistry registry;
            var host = BuildHost(manager, out registry);

            var result = host.Invoke(CommandRequest.Create("host.guide"));
            Assert.IsTrue(result.Ok, result.Error != null ? result.Error.Message : "");
            var text = (string)JToken.FromObject(result.Data);
            StringAssert.Contains(text, "CarroDesk AI 助手接入手册");
            StringAssert.Contains(text, "tools/list");
        }

        [TestMethod]
        public void Hello_CapabilitiesHashChangesWhenRegistryChanges()
        {
            var manager = new ModuleManager();
            var services = new ServiceContainer();
            manager.InitializeAll(services);
            manager.StartAll();

            CommandRegistry registry;
            var host = BuildHost(manager, out registry);
            var before = (string)JToken.FromObject(host.Invoke(CommandRequest.Create("host.hello")).Data)["capabilitiesHash"];

            // 注册表变化 → 能力表哈希变化（客户端据此重取）
            registry.Register(HostCommands.ModuleId, new CommandDescriptor
            {
                Name = "host.test.extra",
                Summary = "临时",
                Risk = CommandRisk.ReadOnly,
                Handler = r => CommandResult.Success()
            });
            var after = (string)JToken.FromObject(host.Invoke(CommandRequest.Create("host.hello")).Data)["capabilitiesHash"];

            Assert.AreNotEqual(before, after);
        }
    }
}
