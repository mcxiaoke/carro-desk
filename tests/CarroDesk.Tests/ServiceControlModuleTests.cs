using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ServiceProcess;
using CarroDesk.Core;
using CarroDesk.Core.Commands;
using CarroDesk.Host.Commands;
using CarroDesk.Host.Services;
using CarroDesk.Modules.ServiceControl;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// S6 验收（IPC 设计 §9.5/§11.2）：services.* 能力——允许清单、口令门、
    /// 异常翻译（拒绝访问/超时/服务不存在）、状态查询。
    /// </summary>
    [TestClass]
    public class ServiceControlModuleTests
    {
        private sealed class FakePinService : IPinService
        {
            public bool IsConfigured { get { return true; } }
            public string Salt { get { return "salt"; } }
            public string Hash { get { return "hash"; } }
            public bool Verify(string pin) { return pin == "1234"; }
            public void SetNewPin(string pin) { }
            public void SetFromConfig(string salt, string hash) { }
        }

        private sealed class FakeServiceAdapter : IServiceControlAdapter
        {
            public readonly Dictionary<string, ServiceControllerStatus> Services =
                new Dictionary<string, ServiceControllerStatus>(StringComparer.OrdinalIgnoreCase);

            public Func<string, Exception> OnStart;
            public Func<string, Exception> OnStop = _ => null;
            public int StartCalls;
            public int StopCalls;

            public ServiceControllerStatus? GetStatus(string serviceName)
            {
                ServiceControllerStatus status;
                return Services.TryGetValue(serviceName, out status) ? status : (ServiceControllerStatus?)null;
            }

            public ServiceControllerStatus Start(string serviceName, TimeSpan timeout)
            {
                StartCalls++;
                if (OnStart != null)
                {
                    var ex = OnStart(serviceName);
                    if (ex != null) throw ex;
                }
                Services[serviceName] = ServiceControllerStatus.Running;
                return ServiceControllerStatus.Running;
            }

            public ServiceControllerStatus Stop(string serviceName, TimeSpan timeout)
            {
                StopCalls++;
                if (OnStop != null)
                {
                    var ex = OnStop(serviceName);
                    if (ex != null) throw ex;
                }
                Services[serviceName] = ServiceControllerStatus.Stopped;
                return ServiceControllerStatus.Stopped;
            }
        }

        private static ServiceControlModule CreateModule(out FakeServiceAdapter adapter, params string[] allowlist)
        {
            adapter = new FakeServiceAdapter();
            var module = new ServiceControlModule(adapter);
            // ModuleBase.Config 默认以空配置保底（无需 Initialize），直接填允许清单
            foreach (var name in allowlist)
            {
                module.Config.AllowedServices.Add(name);
            }
            return module;
        }

        private static CommandHost BuildHost(ServiceControlModule module)
        {
            var registry = new CommandRegistry();
            foreach (var command in module.GetCommands())
            {
                registry.Register(module.Id, command);
            }
            return new CommandHost(
                registry,
                id => ModuleStatus.Running,
                new PinGuard(new FakePinService()),
                new NullCommandAuditSink());
        }

        [TestMethod]
        public void GetCommands_DescriptorsAndEnums()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter, "AlphaSvc", "BetaSvc");

            var commands = new List<CommandDescriptor>(module.GetCommands());
            CollectionAssert.AreEquivalent(
                new[] { "services.status", "services.start", "services.stop" },
                new[] { commands[0].Name, commands[1].Name, commands[2].Name });

            Assert.AreEqual(CommandRisk.ReadOnly, commands[0].Risk);
            Assert.IsFalse(commands[0].RequiresPin);
            Assert.AreEqual(CommandRisk.Privileged, commands[1].Risk);
            Assert.IsTrue(commands[1].RequiresPin);
            Assert.AreEqual(CommandRisk.Privileged, commands[2].Risk);
            Assert.IsTrue(commands[2].RequiresPin);

            foreach (var command in commands)
            {
                var nameParam = command.Params[0];
                CollectionAssert.AreEquivalent(new[] { "AlphaSvc", "BetaSvc" }, nameParam.AllowedValues);
            }
        }

        [TestMethod]
        public void Invoke_Start_RequiresPin_AndCallsAdapter()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter, "AlphaSvc");
            var host = BuildHost(module);

            var noPin = CommandRequest.Create("services.start");
            noPin.Params["name"] = "AlphaSvc";
            Assert.AreEqual(CommandErrorCodes.PinRequired, host.Invoke(noPin).Error.Code);

            var withPin = CommandRequest.Create("services.start");
            withPin.Params["name"] = "AlphaSvc";
            withPin.Pin = "1234";
            var result = host.Invoke(withPin);
            Assert.IsTrue(result.Ok, result.Error != null ? result.Error.Message : "");
            Assert.AreEqual(1, adapter.StartCalls);
        }

        [TestMethod]
        public void Invoke_NotInAllowlist_Rejected()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter, "AlphaSvc");
            var host = BuildHost(module);

            var request = CommandRequest.Create("services.start");
            request.Params["name"] = "EvilSvc";
            request.Pin = "1234";
            var result = host.Invoke(request);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.InvalidParams, result.Error.Code);
            Assert.AreEqual(0, adapter.StartCalls);
        }

        [TestMethod]
        public void Invoke_Stop_HappyPath()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter, "AlphaSvc");
            adapter.Services["AlphaSvc"] = ServiceControllerStatus.Running;
            var host = BuildHost(module);

            var request = CommandRequest.Create("services.stop");
            request.Params["name"] = "AlphaSvc";
            request.Pin = "1234";
            var result = host.Invoke(request);

            Assert.IsTrue(result.Ok, result.Error != null ? result.Error.Message : "");
            Assert.AreEqual(1, adapter.StopCalls);
        }

        [TestMethod]
        public void Invoke_Status_ListsAllowlist_AndSingleLookup()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter, "AlphaSvc", "GhostSvc");
            adapter.Services["AlphaSvc"] = ServiceControllerStatus.Running;
            var host = BuildHost(module);

            var all = host.Invoke(CommandRequest.Create("services.status"));
            Assert.IsTrue(all.Ok);
            var payload = Newtonsoft.Json.Linq.JToken.FromObject(all.Data);
            Assert.AreEqual("Running", (string)payload["services"][0]["status"]);
            Assert.AreEqual("NotFound", (string)payload["services"][1]["status"]);

            var single = CommandRequest.Create("services.status");
            single.Params["name"] = "AlphaSvc";
            var one = host.Invoke(single);
            Assert.AreEqual("Running",
                (string)Newtonsoft.Json.Linq.JToken.FromObject(one.Data)["status"]);
        }

        [TestMethod]
        public void Invoke_AccessDenied_MapsToInternalWithGrantHint()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter, "AlphaSvc");
            adapter.OnStart = _ => new InvalidOperationException(
                "Cannot open AlphaSvc service on computer '.'.",
                new Win32Exception(5, "Access is denied"));
            var host = BuildHost(module);

            var request = CommandRequest.Create("services.start");
            request.Params["name"] = "AlphaSvc";
            request.Pin = "1234";
            var result = host.Invoke(request);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.Internal, result.Error.Code);
            Assert.IsTrue(result.Error.Message.Contains("grant-service-control"));
        }

        [TestMethod]
        public void Invoke_ServiceNotFound_MapsToInvalidParams()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter, "AlphaSvc");
            adapter.OnStart = _ => new InvalidOperationException("service does not exist");
            var host = BuildHost(module);

            var request = CommandRequest.Create("services.start");
            request.Params["name"] = "AlphaSvc";
            request.Pin = "1234";
            var result = host.Invoke(request);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.InvalidParams, result.Error.Code);
        }

        [TestMethod]
        public void Invoke_StartTimeout_MapsToTimeout()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter, "AlphaSvc");
            adapter.OnStart = _ => new System.ServiceProcess.TimeoutException("time out");
            var host = BuildHost(module);

            var request = CommandRequest.Create("services.start");
            request.Params["name"] = "AlphaSvc";
            request.Pin = "1234";
            var result = host.Invoke(request);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.Timeout, result.Error.Code);
        }

        [TestMethod]
        public void CollectCommands_RegistersThroughModuleManager()
        {
            var manager = new ModuleManager();
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter, "AlphaSvc");
            manager.RegisterModule(module);
            manager.InitializeAll(new ServiceContainer());
            manager.StartAll();

            var collected = manager.CollectCommands();
            Assert.IsTrue(collected.Exists(p => p.Value.Name == "services.start"));
            Assert.IsTrue(collected.TrueForAll(p => p.Key == "Services"));
        }
    }
}
