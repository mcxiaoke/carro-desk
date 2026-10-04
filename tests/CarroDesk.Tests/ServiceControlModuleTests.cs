using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ServiceProcess;
using System.Threading;
using CarroDesk.Core;
using CarroDesk.Core.Commands;
using CarroDesk.Host.Commands;
using CarroDesk.Host.Services;
using CarroDesk.Modules.ServiceControl;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace CarroDesk.Tests
{
    /// <summary>
    /// S6 + 按服务口令策略验收（IPC 设计 §9.5/§11.2、SERVICE-CONTROL-PLAN §1/§2）：
    /// 允许清单对象 schema（含旧字符串兼容）、desc 流入参数描述、按服务 PIN 两态、
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
                return Start(serviceName, timeout, CancellationToken.None);
            }

            public ServiceControllerStatus Stop(string serviceName, TimeSpan timeout)
            {
                return Stop(serviceName, timeout, CancellationToken.None);
            }

            public ServiceControllerStatus Start(string serviceName, TimeSpan timeout, CancellationToken token)
            {
                StartCalls++;
                LastStartToken = token;
                if (OnStart != null)
                {
                    var ex = OnStart(serviceName);
                    if (ex != null) throw ex;
                }
                Services[serviceName] = ServiceControllerStatus.Running;
                return ServiceControllerStatus.Running;
            }

            public ServiceControllerStatus Stop(string serviceName, TimeSpan timeout, CancellationToken token)
            {
                StopCalls++;
                LastStopToken = token;
                if (OnStop != null)
                {
                    var ex = OnStop(serviceName);
                    if (ex != null) throw ex;
                }
                Services[serviceName] = ServiceControllerStatus.Stopped;
                return ServiceControllerStatus.Stopped;
            }

            /// <summary>最近一次启停收到的取消令牌（验证内核确实把令牌透传到了适配器）。</summary>
            public CancellationToken LastStartToken;
            public CancellationToken LastStopToken;
        }

        private static ServiceControlModule CreateModule(out FakeServiceAdapter adapter, params ServiceAllowlistEntry[] allowlist)
        {
            adapter = new FakeServiceAdapter();
            var module = new ServiceControlModule(adapter);
            // ModuleBase.Config 默认以空配置保底（无需 Initialize），直接填允许清单
            module.Config.AllowedServices.AddRange(allowlist);
            return module;
        }

        private static ServiceAllowlistEntry Entry(string name, string desc = null, bool requiresPin = true)
        {
            return new ServiceAllowlistEntry { Name = name, Desc = desc, RequiresPin = requiresPin };
        }

        private static CommandHost BuildHost(ServiceControlModule module, ICommandAuditSink audit = null)
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
                audit ?? new NullCommandAuditSink());
        }

        private static CommandRequest Call(string method, string serviceName, string pin = null)
        {
            var request = CommandRequest.Create(method);
            request.Params["name"] = serviceName;
            request.Pin = pin;
            return request;
        }

        [TestMethod]
        public void GetCommands_DescriptorsEnumsAndDescFlow()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter,
                Entry("GameViewerService", "UUYC 远程控制", requiresPin: false),
                Entry("TermService", "RDP 远程桌面"));

            var commands = new List<CommandDescriptor>(module.GetCommands());
            CollectionAssert.AreEquivalent(
                new[] { "services.status", "services.start", "services.stop" },
                new[] { commands[0].Name, commands[1].Name, commands[2].Name });

            Assert.AreEqual(CommandRisk.ReadOnly, commands[0].Risk);
            Assert.AreEqual(CommandRisk.Privileged, commands[1].Risk);
            Assert.AreEqual(CommandRisk.Privileged, commands[2].Risk);

            foreach (var command in commands)
            {
                var nameParam = command.Params[0];
                CollectionAssert.AreEquivalent(new[] { "GameViewerService", "TermService" }, nameParam.AllowedValues);
                // desc 流入参数描述：AI 能把「开 UU远程」映射到 GameViewerService
                StringAssert.Contains(nameParam.Description, "GameViewerService=UUYC 远程控制");
                StringAssert.Contains(nameParam.Description, "TermService=RDP 远程桌面");
            }

            // 静态 RequiresPin 关闭（改由 RequiresPinFor 按服务判定），timeoutMs 为任务级 30s
            Assert.IsFalse(commands[1].RequiresPin);
            Assert.IsNotNull(commands[1].RequiresPinFor);
            Assert.AreEqual(30000, commands[1].TimeoutMs);

            // services.start/stop 必须走可取消执行体：这是本仓最长的能力（等 SCM 状态变化），
            // 用普通 Handler 就只能"停止等待"，内核超时后适配器还会继续阻塞到自己的 15s
            Assert.IsNotNull(commands[1].CancellableHandler, "services.start 应声明 CancellableHandler");
            Assert.IsNotNull(commands[2].CancellableHandler, "services.stop 应声明 CancellableHandler");
        }

        /// <summary>内核必须把取消令牌透传到适配器，否则超时后仍在傻等。</summary>
        [TestMethod]
        public void Invoke_StartStop_ForwardsCancellationTokenToAdapter()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter, Entry("GameViewerService", requiresPin: false));
            var host = BuildHost(module);

            Assert.IsTrue(host.Invoke(Call("services.start", "GameViewerService")).Ok);
            Assert.IsTrue(adapter.LastStartToken.CanBeCanceled, "适配器应收到可取消令牌");

            Assert.IsTrue(host.Invoke(Call("services.stop", "GameViewerService")).Ok);
            Assert.IsTrue(adapter.LastStopToken.CanBeCanceled, "适配器应收到可取消令牌");
        }

        [TestMethod]
        public void Invoke_PerServicePinPolicy_FreeWithoutPin_RequiredWithPin()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter,
                Entry("GameViewerService", requiresPin: false),
                Entry("TermService", requiresPin: true));
            var host = BuildHost(module);

            // 免 PIN 服务：不带口令直接成功
            var free = host.Invoke(Call("services.start", "GameViewerService"));
            Assert.IsTrue(free.Ok, free.Error != null ? free.Error.Message : "");
            Assert.AreEqual(1, adapter.StartCalls);

            // 要求口令的服务：缺口令拒绝；对口令成功
            var noPin = host.Invoke(Call("services.start", "TermService"));
            Assert.AreEqual(CommandErrorCodes.PinRequired, noPin.Error.Code);

            var withPin = host.Invoke(Call("services.start", "TermService", "1234"));
            Assert.IsTrue(withPin.Ok, withPin.Error != null ? withPin.Error.Message : "");
            Assert.AreEqual(2, adapter.StartCalls);

            // stop 同样按策略
            Assert.AreEqual(CommandErrorCodes.PinRequired,
                host.Invoke(Call("services.stop", "TermService")).Error.Code);
            Assert.IsTrue(host.Invoke(Call("services.stop", "GameViewerService")).Ok);
        }

        [TestMethod]
        public void Invoke_NotInAllowlist_Rejected()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter, Entry("AlphaSvc"));
            var host = BuildHost(module);

            var request = Call("services.start", "EvilSvc", "1234");
            var result = host.Invoke(request);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.InvalidParams, result.Error.Code);
            Assert.AreEqual(0, adapter.StartCalls);
        }

        [TestMethod]
        public void Invoke_Status_ListsAllowlistWithDescAndPinPolicy()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter,
                Entry("AlphaSvc", "测试服务", requiresPin: false),
                Entry("GhostSvc"));
            adapter.Services["AlphaSvc"] = ServiceControllerStatus.Running;
            var host = BuildHost(module);

            var all = host.Invoke(CommandRequest.Create("services.status"));
            Assert.IsTrue(all.Ok);
            var payload = Newtonsoft.Json.Linq.JToken.FromObject(all.Data);
            Assert.AreEqual("Running", (string)payload["services"][0]["status"]);
            Assert.AreEqual("测试服务", (string)payload["services"][0]["desc"]);
            Assert.AreEqual(false, (bool)payload["services"][0]["requiresPin"]);
            Assert.AreEqual(true, (bool)payload["services"][1]["requiresPin"]);
            Assert.AreEqual("NotFound", (string)payload["services"][1]["status"]);

            var single = Call("services.status", "AlphaSvc");
            var one = host.Invoke(single);
            Assert.AreEqual("Running",
                (string)Newtonsoft.Json.Linq.JToken.FromObject(one.Data)["status"]);
        }

        [TestMethod]
        public void Invoke_AccessDenied_MapsToInternalWithGrantHint()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter, Entry("AlphaSvc"));
            adapter.OnStart = _ => new InvalidOperationException(
                "Cannot open AlphaSvc service on computer '.'.",
                new Win32Exception(5, "Access is denied"));
            var host = BuildHost(module);

            var result = host.Invoke(Call("services.start", "AlphaSvc", "1234"));

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.Internal, result.Error.Code);
            Assert.IsTrue(result.Error.Message.Contains("grant-service-control"));
        }

        [TestMethod]
        public void Invoke_ServiceNotFound_MapsToInvalidParams()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter, Entry("AlphaSvc"));
            adapter.OnStart = _ => new InvalidOperationException("service does not exist");
            var host = BuildHost(module);

            var result = host.Invoke(Call("services.start", "AlphaSvc", "1234"));

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.InvalidParams, result.Error.Code);
        }

        [TestMethod]
        public void Invoke_StartTimeout_MapsToTimeout()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter, Entry("AlphaSvc"));
            adapter.OnStart = _ => new System.ServiceProcess.TimeoutException("time out");
            var host = BuildHost(module);

            var result = host.Invoke(Call("services.start", "AlphaSvc", "1234"));

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.Timeout, result.Error.Code);
        }

        [TestMethod]
        public void CollectCommands_RegistersThroughModuleManager()
        {
            var manager = new ModuleManager();
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter, Entry("AlphaSvc"));
            manager.RegisterModule(module);
            manager.InitializeAll(new ServiceContainer());
            manager.StartAll();

            var collected = manager.CollectCommands();
            Assert.IsTrue(collected.Exists(p => p.Value.Name == "services.start"));
            Assert.IsTrue(collected.TrueForAll(p => p.Key == "Services"));
        }

        // ---------- 配置 schema 兼容（SERVICE-CONTROL-PLAN §1.1） ----------

        [TestMethod]
        public void Config_ObjectEntries_And_LegacyStringEntries_BothParse()
        {
            const string json = "{\"AllowedServices\":[" +
                                "{\"name\":\"GameViewerService\",\"desc\":\"UUYC 远程控制\",\"requiresPin\":false}," +
                                "\"TermService\"," +
                                "{\"name\":\"Spooler\",\"pin\":false}," +
                                "{\"name\":\"NoPinField\"}" +
                                "]}";
            var config = JsonConvert.DeserializeObject<ServiceControlConfig>(json);

            Assert.AreEqual(4, config.AllowedServices.Count);

            var game = config.AllowedServices[0];
            Assert.AreEqual("GameViewerService", game.Name);
            Assert.AreEqual("UUYC 远程控制", game.Desc);
            Assert.IsFalse(game.RequiresPin);

            // 旧字符串写法 → desc 空、requiresPin=true
            var term = config.AllowedServices[1];
            Assert.AreEqual("TermService", term.Name);
            Assert.IsNull(term.Desc);
            Assert.IsTrue(term.RequiresPin);

            // pin 别名
            Assert.IsFalse(config.AllowedServices[2].RequiresPin);

            // 缺省 → fail-safe true
            Assert.IsTrue(config.AllowedServices[3].RequiresPin);
        }

        [TestMethod]
        public void Config_RoundTrip_WritesObjectForm()
        {
            var config = new ServiceControlConfig();
            config.AllowedServices.Add(new ServiceAllowlistEntry { Name = "GameViewerService", Desc = "UUYC", RequiresPin = false });

            var json = JsonConvert.SerializeObject(config);
            var roundTripped = JsonConvert.DeserializeObject<ServiceControlConfig>(json);

            Assert.AreEqual("GameViewerService", roundTripped.AllowedServices[0].Name);
            Assert.AreEqual("UUYC", roundTripped.AllowedServices[0].Desc);
            Assert.IsFalse(roundTripped.AllowedServices[0].RequiresPin);
        }

        [TestMethod]
        public void Config_DuplicateNames_AreDeduplicatedKeepingFirstPin()
        {
            FakeServiceAdapter adapter;
            var module = CreateModule(out adapter,
                Entry("AlphaSvc", "第一个", requiresPin: false),
                Entry("ALPHASVC", "第二个", requiresPin: true));

            var host = BuildHost(module);
            // 去重保留首个条目的策略（requiresPin=false）→ 无口令可启
            var result = host.Invoke(Call("services.start", "AlphaSvc"));
            Assert.IsTrue(result.Ok, result.Error != null ? result.Error.Message : "");

            var status = host.Invoke(CommandRequest.Create("services.status"));
            var payload = Newtonsoft.Json.Linq.JToken.FromObject(status.Data);
            Assert.AreEqual(1, ((Newtonsoft.Json.Linq.JArray)payload["services"]).Count);
            Assert.AreEqual("第一个", (string)payload["services"][0]["desc"]);
        }
    }
}
