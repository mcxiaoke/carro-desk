using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CarroDesk.Core;
using CarroDesk.Core.Commands;
using CarroDesk.Core.Models;
using CarroDesk.Host.Commands;
using CarroDesk.Host.Services;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// S1 命令内核验收（IPC 设计 §8）：白名单拒绝、参数校验、模块非 Running 拒绝、
    /// 超时、异常不逃逸；另覆盖口令门、失败限流、限流器与审计脱敏。
    /// </summary>
    [TestClass]
    public class CommandHostTests
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

        private sealed class MemoryAuditSink : ICommandAuditSink
        {
            public readonly List<CommandAuditEntry> Entries = new List<CommandAuditEntry>();
            public void Write(CommandAuditEntry entry) { Entries.Add(entry); }
        }

        private sealed class CommandedModule : ModuleBase<DummyConfig>, ICommandProvider
        {
            private readonly string _id;
            private readonly bool _throwOnCollect;

            public CommandedModule(string id, bool throwOnCollect = false)
            {
                _id = id;
                _throwOnCollect = throwOnCollect;
            }

            public override string Id { get { return _id; } }
            public override string Name { get { return _id; } }
            protected override void OnStart() { }
            protected override void OnStop() { }

            public IEnumerable<CommandDescriptor> GetCommands()
            {
                if (_throwOnCollect) throw new InvalidOperationException("collect crash");

                yield return new CommandDescriptor
                {
                    Name = _id + ".ping",
                    Summary = "只读探测",
                    Risk = CommandRisk.ReadOnly,
                    Handler = r => CommandResult.Success("pong")
                };
            }
        }

        private static CommandDescriptor SimpleCommand(
            string name = "test.echo",
            CommandRisk risk = CommandRisk.ReadOnly,
            bool requiresPin = false,
            int timeoutMs = 0,
            CommandParam[] parameters = null,
            Func<CommandRequest, CommandResult> handler = null)
        {
            return new CommandDescriptor
            {
                Name = name,
                Summary = "test",
                Risk = risk,
                RequiresPin = requiresPin,
                TimeoutMs = timeoutMs,
                Params = parameters,
                Handler = handler ?? (r => CommandResult.Success("ok"))
            };
        }

        private static CommandHost BuildHost(
            Action<CommandRegistry> setup,
            Func<string, ModuleStatus> status = null,
            PinGuard pinGuard = null,
            CommandRateLimiter rateLimiter = null,
            ICommandAuditSink audit = null)
        {
            var registry = new CommandRegistry();
            setup?.Invoke(registry);
            return new CommandHost(
                registry,
                status ?? (id => ModuleStatus.Running),
                pinGuard,
                audit ?? new MemoryAuditSink(),
                rateLimiter);
        }

        [TestMethod]
        public void Invoke_UnknownCapability_ReturnsCapabilityNotFound()
        {
            var host = BuildHost(null);
            var result = host.Invoke(CommandRequest.Create("nope.nope"));
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.CapabilityNotFound, result.Error.Code);
        }

        [TestMethod]
        public void Invoke_NullOrEmptyMethod_ReturnsCapabilityNotFound()
        {
            var host = BuildHost(null);
            Assert.AreEqual(CommandErrorCodes.CapabilityNotFound, host.Invoke(null).Error.Code);
            Assert.AreEqual(CommandErrorCodes.CapabilityNotFound, host.Invoke(CommandRequest.Create("")).Error.Code);
        }

        [TestMethod]
        public void Invoke_ModuleNotRunning_ReturnsModuleUnavailable()
        {
            var host = BuildHost(
                r => r.Register("test", SimpleCommand()),
                status: id => ModuleStatus.Stopped);
            var result = host.Invoke(CommandRequest.Create("test.echo"));
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.ModuleUnavailable, result.Error.Code);
        }

        [TestMethod]
        public void Invoke_MissingRequiredParam_ReturnsInvalidParams()
        {
            var host = BuildHost(r => r.Register("test", SimpleCommand(parameters: new[]
            {
                new CommandParam { Name = "minutes", Type = "int", Required = true }
            })));
            var result = host.Invoke(CommandRequest.Create("test.echo"));
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.InvalidParams, result.Error.Code);
            Assert.IsTrue(result.Error.Message.Contains("minutes"));
        }

        [TestMethod]
        public void Invoke_UnknownParamName_Rejected()
        {
            var host = BuildHost(r => r.Register("test", SimpleCommand()));
            var request = CommandRequest.Create("test.echo");
            request.Params["extra"] = 1;
            var result = host.Invoke(request);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.InvalidParams, result.Error.Code);
            Assert.IsTrue(result.Error.Message.Contains("extra"));
        }

        [TestMethod]
        public void Invoke_ParamTypeMismatch_Rejected()
        {
            var host = BuildHost(r => r.Register("test", SimpleCommand(parameters: new[]
            {
                new CommandParam { Name = "minutes", Type = "int", Required = true }
            })));
            var request = CommandRequest.Create("test.echo");
            request.Params["minutes"] = "abc";
            var result = host.Invoke(request);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.InvalidParams, result.Error.Code);
        }

        [TestMethod]
        public void Invoke_CoercibleStringParam_NormalizedToInt()
        {
            string delivered = null;
            var host = BuildHost(r => r.Register("test", SimpleCommand(parameters: new[]
            {
                new CommandParam { Name = "minutes", Type = "int", Required = true }
            }, handler: req =>
            {
                delivered = req.Params["minutes"].GetType().Name + ":" + req.Params["minutes"];
                return CommandResult.Success();
            })));
            var request = CommandRequest.Create("test.echo");
            request.Params["minutes"] = "15";
            var result = host.Invoke(request);
            Assert.IsTrue(result.Ok, result.Error != null ? result.Error.Message : "");
            Assert.AreEqual("Int32:15", delivered);
        }

        [TestMethod]
        public void Invoke_AllowedValuesViolation_Rejected()
        {
            var host = BuildHost(r => r.Register("test", SimpleCommand(parameters: new[]
            {
                new CommandParam { Name = "mode", Type = "string", Required = true, AllowedValues = new[] { "on", "off" } }
            })));
            var request = CommandRequest.Create("test.echo");
            request.Params["mode"] = "sideways";
            var result = host.Invoke(request);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.InvalidParams, result.Error.Code);
            Assert.IsTrue(result.Error.Message.Contains("on, off"));
        }

        [TestMethod]
        public void Invoke_PinRequired_MissingOrWrongRejected_CorrectExecutes()
        {
            var host = BuildHost(
                r => r.Register("test", SimpleCommand(requiresPin: true)),
                pinGuard: new PinGuard(new FakePinService()));

            var missing = host.Invoke(CommandRequest.Create("test.echo"));
            Assert.AreEqual(CommandErrorCodes.PinRequired, missing.Error.Code);

            var wrong = CommandRequest.Create("test.echo");
            wrong.Pin = "0000";
            Assert.AreEqual(CommandErrorCodes.PinRequired, host.Invoke(wrong).Error.Code);

            var right = CommandRequest.Create("test.echo");
            right.Pin = "1234";
            Assert.IsTrue(host.Invoke(right).Ok);
        }

        [TestMethod]
        public void Invoke_PinGuardBlocked_StillReturnsPinRequired()
        {
            var host = BuildHost(
                r => r.Register("test", SimpleCommand(requiresPin: true)),
                pinGuard: new PinGuard(new FakePinService()));

            // PinGuard 5 次失败后进入封锁期；第 6 次即使口令正确也返回 Blocked（仍是 -32002）
            for (int i = 0; i < 5; i++)
            {
                var bad = CommandRequest.Create("test.echo");
                bad.Pin = "0000";
                Assert.AreEqual(CommandErrorCodes.PinRequired, host.Invoke(bad).Error.Code);
            }

            var blocked = CommandRequest.Create("test.echo");
            blocked.Pin = "1234";
            var result = host.Invoke(blocked);
            Assert.AreEqual(CommandErrorCodes.PinRequired, result.Error.Code);
            Assert.IsTrue(result.Error.Message.Contains("blocked"));
        }

        [TestMethod]
        public void Invoke_PinRequired_WithoutGuard_FailsClosed()
        {
            var host = BuildHost(r => r.Register("test", SimpleCommand(requiresPin: true)), pinGuard: null);
            var right = CommandRequest.Create("test.echo");
            right.Pin = "1234";
            Assert.AreEqual(CommandErrorCodes.PinRequired, host.Invoke(right).Error.Code);
        }

        [TestMethod]
        public void Invoke_HandlerThrows_FoldedToInternalError_NoEscape()
        {
            var host = BuildHost(r => r.Register("test", SimpleCommand(handler: req =>
            {
                throw new InvalidOperationException("boom");
            })));
            var result = host.Invoke(CommandRequest.Create("test.echo"));
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.Internal, result.Error.Code);
            Assert.IsTrue(result.Error.Message.Contains("boom"));
            // 错误消息不含堆栈
            Assert.IsFalse(result.Error.Message.Contains("at CarroDesk"));
        }

        [TestMethod]
        public void Invoke_HandlerTimeout_ReturnsTimeout()
        {
            var host = BuildHost(r => r.Register("test", SimpleCommand(
                timeoutMs: 150,
                handler: req => { Thread.Sleep(5000); return CommandResult.Success(); })));

            var sw = Stopwatch.StartNew();
            var result = host.Invoke(CommandRequest.Create("test.echo"));
            sw.Stop();

            Assert.AreEqual(CommandErrorCodes.Timeout, result.Error.Code);
            Assert.IsTrue(sw.ElapsedMilliseconds < 3000, "Invoke 应在超时后尽快返回，而不是等 handler 跑完");
        }

        [TestMethod]
        public void Invoke_RateLimitExceeded_ReturnsRateLimited()
        {
            var host = BuildHost(
                r => r.Register("test", SimpleCommand()),
                rateLimiter: new CommandRateLimiter(1, TimeSpan.FromMinutes(1)));

            Assert.IsTrue(host.Invoke(CommandRequest.Create("test.echo")).Ok);
            var second = host.Invoke(CommandRequest.Create("test.echo"));
            Assert.IsFalse(second.Ok);
            Assert.AreEqual(CommandErrorCodes.RateLimited, second.Error.Code);
        }

        [TestMethod]
        public void Invoke_RateLimit_IsPerSource()
        {
            var host = BuildHost(
                r => r.Register("test", SimpleCommand()),
                rateLimiter: new CommandRateLimiter(1, TimeSpan.FromMinutes(1)));

            Assert.IsTrue(host.Invoke(CommandRequest.Create("test.echo", "cli")).Ok);
            Assert.IsTrue(host.Invoke(CommandRequest.Create("test.echo", "mcp")).Ok);
        }

        [TestMethod]
        public void Invoke_Success_AuditsWithoutSecrets()
        {
            var audit = new MemoryAuditSink();
            var host = BuildHost(
                r => r.Register("test", SimpleCommand(requiresPin: true, parameters: new[]
                {
                    new CommandParam { Name = "name", Type = "string" }
                })),
                pinGuard: new PinGuard(new FakePinService()),
                audit: audit);

            var request = CommandRequest.Create("test.echo", "mcp");
            request.Params["name"] = "super-secret-value";
            request.Pin = "1234";
            Assert.IsTrue(host.Invoke(request).Ok);

            Assert.AreEqual(1, audit.Entries.Count);
            var entry = audit.Entries[0];
            Assert.IsTrue(entry.Ok);
            Assert.AreEqual("test.echo", entry.Method);
            Assert.AreEqual("mcp", entry.Source);
            Assert.IsNotNull(entry.ParamsDigest);
            Assert.IsFalse(entry.ParamsDigest.Contains("super-secret-value"));

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(entry);
            Assert.IsFalse(json.Contains("super-secret-value"), "参数原文不得落审计");
            Assert.IsFalse(json.Contains("1234"), "口令原文不得落审计");
        }

        [TestMethod]
        public void Invoke_Failure_AlsoAudited()
        {
            var audit = new MemoryAuditSink();
            var host = BuildHost(r => r.Register("test", SimpleCommand()), audit: audit);
            Assert.IsFalse(host.Invoke(CommandRequest.Create("test.nope")).Ok);
            Assert.AreEqual(1, audit.Entries.Count);
            Assert.IsFalse(audit.Entries[0].Ok);
            Assert.AreEqual(CommandErrorCodes.CapabilityNotFound, audit.Entries[0].Code);
        }

        [TestMethod]
        public void Registry_DuplicateName_Throws()
        {
            var registry = new CommandRegistry();
            registry.Register("a", SimpleCommand("dup.cmd"));
            Assert.ThrowsException<InvalidOperationException>(() => registry.Register("b", SimpleCommand("DUP.CMD")));
        }

        [TestMethod]
        public void Registry_MissingHandler_Throws()
        {
            var registry = new CommandRegistry();
            var noHandler = new CommandDescriptor { Name = "bad.cmd", Summary = "test" };
            Assert.ThrowsException<ArgumentException>(() => registry.Register("a", noHandler));
        }

        [TestMethod]
        public void Registry_TryGet_FillsModuleId_CaseInsensitive()
        {
            var registry = new CommandRegistry();
            var cmd = SimpleCommand("mod.echo");
            registry.Register("Mod", cmd);

            CommandDescriptor found;
            Assert.IsTrue(registry.TryGet("MOD.ECHO", out found));
            Assert.AreSame(cmd, found);
            Assert.AreEqual("Mod", found.ModuleId);
        }

        [TestMethod]
        public void Registry_Snapshot_SortedByName()
        {
            var registry = new CommandRegistry();
            registry.Register("a", SimpleCommand("a.zeta"));
            registry.Register("a", SimpleCommand("a.alpha"));
            var names = registry.Snapshot().Select(c => c.Name).ToList();
            CollectionAssert.AreEqual(new[] { "a.alpha", "a.zeta" }, names);
        }

        [TestMethod]
        public void CollectCommands_IsolatesFailingProvider_AndFillsModuleId()
        {
            var manager = new ModuleManager();
            manager.RegisterModule(new CommandedModule("good"));
            manager.RegisterModule(new CommandedModule("bad", throwOnCollect: true));

            var collected = manager.CollectCommands();

            Assert.AreEqual(1, collected.Count);
            Assert.AreEqual("good", collected[0].Key);
            Assert.AreEqual("good.ping", collected[0].Value.Name);
        }

        [TestMethod]
        public void CollectCommands_EndToEnd_InvokeThroughHost()
        {
            var manager = new ModuleManager();
            manager.RegisterModule(new CommandedModule("mod"));
            var services = new ServiceContainer();
            manager.InitializeAll(services);
            manager.StartAll();

            var registry = new CommandRegistry();
            registry.Rebuild(manager.CollectCommands());

            var host = new CommandHost(
                registry,
                id => manager.GetModuleStatus(id),
                null,
                new MemoryAuditSink());

            var result = host.Invoke(CommandRequest.Create("mod.ping"));
            Assert.IsTrue(result.Ok, result.Error != null ? result.Error.Message : "");
            Assert.AreEqual("pong", result.Data);
        }

        [TestMethod]
        public void RateLimiter_WindowReset_AfterElapsed()
        {
            var now = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
            var limiter = new CommandRateLimiter(1, TimeSpan.FromSeconds(10), () => now);

            Assert.IsTrue(limiter.TryAcquire("m", "s"));
            Assert.IsFalse(limiter.TryAcquire("m", "s"));

            now = now.AddSeconds(11);
            Assert.IsTrue(limiter.TryAcquire("m", "s"));
        }

        [TestMethod]
        public void AuditDigest_DoesNotLeakValues_AndRotatesFile()
        {
            var digest = FileCommandAuditSink.DigestParams(new Dictionary<string, object>
            {
                { "name", "top-secret" }
            });
            Assert.IsFalse(digest.Contains("top-secret"));
            Assert.IsTrue(digest.StartsWith("sha256:", StringComparison.Ordinal));
            Assert.IsTrue(digest.Contains("len="));

            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "carrodesk-audit-test-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                var sink = new FileCommandAuditSink(path, maxBytes: 1);
                sink.Write(new CommandAuditEntry { Method = "a", Ok = true });
                sink.Write(new CommandAuditEntry { Method = "b", Ok = true });
                sink.Write(new CommandAuditEntry { Method = "c", Ok = true });

                Assert.IsTrue(System.IO.File.Exists(path));
                Assert.IsTrue(System.IO.File.Exists(path + ".1"), "超过 maxBytes 应轮转出 .1 备份");
                var current = System.IO.File.ReadAllText(path);
                Assert.IsTrue(current.Contains("\"Method\":\"c\"") || current.Contains("\"Method\": \"c\""), "主文件应保留最新记录");
            }
            finally
            {
                TryDelete(path);
                TryDelete(path + ".1");
            }
        }

        private static void TryDelete(string path)
        {
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
        }

        private sealed class DummyConfig { }
    }
}
