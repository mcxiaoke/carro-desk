using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

            /// <summary>
            /// 并发命令会同时写审计（每条命令写一条），必须加锁：
            /// 否则 List.Add 的内部数组扩容会在并发下抛 IndexOutOfRangeException，
            /// 表现为随机失败 —— 这是测试替身自身的线程安全问题，不是被测代码的缺陷。
            /// </summary>
            public void Write(CommandAuditEntry entry) { lock (Entries) Entries.Add(entry); }

            /// <summary>读取快照（加锁拷贝，避免与并发写入争用）。</summary>
            public List<CommandAuditEntry> Snapshot()
            {
                lock (Entries) return new List<CommandAuditEntry>(Entries);
            }
        }

        private sealed class RecordingLogger : ILoggerService
        {
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> Errors = new List<string>();
            public void LogInfo(string module, string message) { }
            public void LogWarning(string module, string message)
            {
                lock (Warnings) Warnings.Add(module + "|" + message);
            }
            public void LogError(string module, string message, Exception ex = null)
            {
                lock (Errors) Errors.Add(module + "|" + message + "|" + (ex != null ? ex.Message : ""));
            }
            public bool AnyWarningContains(string fragment)
            {
                lock (Warnings)
                    foreach (var w in Warnings) if (w.Contains(fragment)) return true;
                return false;
            }
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
            ICommandAuditSink audit = null,
            ILoggerService logger = null)
        {
            var registry = new CommandRegistry();
            setup?.Invoke(registry);
            return new CommandHost(
                registry,
                status ?? (id => ModuleStatus.Running),
                pinGuard,
                audit ?? new MemoryAuditSink(),
                rateLimiter,
                logger);
        }

        [TestMethod]
        public void Invoke_IntParam_AcceptsIntegralJsonNumber()
        {
            // JSON 5.0 会被解析为 double；int 参数应按整数语义收敛，而不是静默降级为字符串
            object received = null;
            var host = BuildHost(r => r.Register("test", SimpleCommand(parameters: new[]
            {
                new CommandParam { Name = "minutes", Type = "int", Required = true }
            }, handler: call => { received = call.Params["minutes"]; return CommandResult.Success(); })));

            var req = CommandRequest.Create("test.echo");
            req.Params["minutes"] = 5.0d;
            var result = host.Invoke(req);

            Assert.IsTrue(result.Ok, result.Error != null ? result.Error.Message : "");
            Assert.IsInstanceOfType(received, typeof(int));
            Assert.AreEqual(5, received);
        }

        [TestMethod]
        public void Invoke_IntParam_RejectsNonIntegralJsonNumber()
        {
            var host = BuildHost(r => r.Register("test", SimpleCommand(parameters: new[]
            {
                new CommandParam { Name = "minutes", Type = "int", Required = true }
            })));

            var req = CommandRequest.Create("test.echo");
            req.Params["minutes"] = 5.5d;
            var result = host.Invoke(req);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(CommandErrorCodes.InvalidParams, result.Error.Code);
        }

        [TestMethod]
        public void CommandRegistry_Rebuild_OnDuplicate_ThrowsAndRetainsPreviousState()
        {
            var registry = new CommandRegistry();
            registry.Register("boot", SimpleCommand(name: "boot.one"));

            var collected = new List<KeyValuePair<string, CommandDescriptor>>
            {
                new KeyValuePair<string, CommandDescriptor>("a", SimpleCommand(name: "a.x")),
                new KeyValuePair<string, CommandDescriptor>("b", SimpleCommand(name: "b.y")),
                new KeyValuePair<string, CommandDescriptor>("c", SimpleCommand(name: "a.x")) // 与 a.x 重复
            };

            bool threw = false;
            try { registry.Rebuild(collected); }
            catch (InvalidOperationException) { threw = true; }

            Assert.IsTrue(threw, "重复能力名应导致 Rebuild 抛出");
            // 原子替换：失败时保留原有效能力表，绝不停留在"半注册"状态
            Assert.AreEqual(1, registry.Count);
            CommandDescriptor desc;
            Assert.IsTrue(registry.TryGet("boot.one", out desc), "失败后应完整保留既有能力");
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

        /// <summary>
        /// 超时必须发出取消信号：否则"忽略超时的能力"无法知道该收手，
        /// 用户收到 -32004 后后台仍在继续（审计也只留下超时那一刻）。
        /// </summary>
        [TestMethod]
        public void Invoke_Timeout_CancelsCancellableHandler()
        {
            var observed = new ManualResetEventSlim(false);
            CancellationToken tokenSeen = default(CancellationToken);

            var descriptor = new CommandDescriptor
            {
                Name = "test.echo",
                ModuleId = "test",
                Summary = "test",
                Risk = CommandRisk.ReadOnly,
                TimeoutMs = 200,
                CancellableHandler = (req, token) =>
                {
                    tokenSeen = token;
                    // 模拟真实的长耗时轮询：观察取消信号后立刻退出
                    for (int i = 0; i < 100; i++)
                    {
                        if (token.IsCancellationRequested) break;
                        Thread.Sleep(20);
                    }
                    observed.Set();
                    return CommandResult.Success("should-not-be-used");
                }
            };

            var host = BuildHost(r => r.Register("test", descriptor));
            var result = host.Invoke(CommandRequest.Create("test.echo"));

            Assert.AreEqual(CommandErrorCodes.Timeout, result.Error.Code);
            Assert.IsTrue(observed.Wait(TimeSpan.FromSeconds(5)), "handler 应在收到取消后退出");
            Assert.IsTrue(tokenSeen.CanBeCanceled, "handler 必须拿到可取消的令牌");
            Assert.IsTrue(tokenSeen.IsCancellationRequested, "超时应触发 CancellationToken");
        }

        /// <summary>
        /// 不响应取消的 handler：内核不能被它拖住（照常按超时返回），
        /// 但必须观察它稍后抛出的异常 —— 否则会变成 UnobservedTaskException，
        /// handler 的真实失败原因就此静默丢失。
        /// </summary>
        [TestMethod]
        public void Invoke_Timeout_IgnoringHandler_LateExceptionIsObservedAndLogged()
        {
            var unobserved = new List<Exception>();
            EventHandler<UnobservedTaskExceptionEventArgs> handler = (s, e) =>
            {
                lock (unobserved) unobserved.Add(e.Exception);
                e.SetObserved();
            };
            TaskScheduler.UnobservedTaskException += handler;
            try
            {
                var logger = new RecordingLogger();
                var finished = new ManualResetEventSlim(false);

                var descriptor = new CommandDescriptor
                {
                    Name = "test.echo",
                    ModuleId = "test",
                    Summary = "test",
                    Risk = CommandRisk.ReadOnly,
                    TimeoutMs = 150,
                    // 完全忽略令牌：睡到超时之后才抛
                    Handler = req =>
                    {
                        Thread.Sleep(600);
                        finished.Set();
                        throw new InvalidOperationException("late-boom");
                    }
                };

                var host = BuildHost(r => r.Register("test", descriptor), logger: logger);
                var result = host.Invoke(CommandRequest.Create("test.echo"));

                Assert.AreEqual(CommandErrorCodes.Timeout, result.Error.Code,
                    "不响应取消的 handler 不应让内核一直等下去");
                Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(5)));

                // 强制 GC 触发未观察异常的终结流程
                for (int i = 0; i < 3; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }

                lock (unobserved)
                    Assert.AreEqual(0, unobserved.Count,
                        "超时后 handler 的异常必须已被内核观察（否则会成为 UnobservedTaskException）");

                // 并且留下可排障的日志：调用方拿到的是超时，真实原因只有这里有
                Assert.IsTrue(logger.Errors.Exists(m => m.Contains("late-boom")),
                    "超时后 handler 的异常应记入日志（实际记录: " +
                    string.Join(" | ", logger.Errors.ToArray()) + ")");
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= handler;
            }
        }

        /// <summary>
        /// 等待期间不得占用线程：老实现是 Task.Wait(timeout)，
        /// 每条并发命令白占两个线程池线程（handler 一个、纯等待一个）。
        /// 这里用"同时发起大量慢命令"验证吞吐不再被等待线程数限制。
        /// </summary>
        [TestMethod]
        public void InvokeAsync_ConcurrentSlowCommands_DoNotConsumeOneThreadPerCall()
        {
            const int concurrency = 60;
            var host = BuildHost(r => r.Register("test", SimpleCommand(
                timeoutMs: 30000,
                handler: req => { Thread.Sleep(300); return CommandResult.Success(); })));

            int peakThreads = 0;
            int baseline = 0;
            // 用信号事件结束采样线程：Thread.Interrupt 抛出的异常在现代 .NET
            // 的测试主机上按未处理异常处理，会崩掉测试进程
            using (var samplerStop = new ManualResetEvent(false))
            {
                var sampler = new Thread(() =>
                {
                    while (!samplerStop.WaitOne(15))
                    {
                        try { peakThreads = Math.Max(peakThreads, Process.GetCurrentProcess().Threads.Count); }
                        catch { return; }
                    }
                });
                baseline = Process.GetCurrentProcess().Threads.Count;

                var tasks = new List<Task<CommandResult>>();
                for (int i = 0; i < concurrency; i++)
                    tasks.Add(host.InvokeAsync(CommandRequest.Create("test.echo", "src" + i)));

                sampler.Start();
                Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(60));
                samplerStop.Set();
                sampler.Join(1000);

                int succeeded = 0;
                foreach (var t in tasks) if (t.Result != null && t.Result.Ok) succeeded++;
                Assert.AreEqual(concurrency, succeeded, "所有慢命令都应正常完成");
            }

            // 60 条并发若每条占 2 个线程池线程会明显膨胀；给出宽松上界只抓回归
            int grew = peakThreads - baseline;
            Assert.IsTrue(grew < concurrency,
                "等待期间不应按并发数线性占用线程（线程数增长 " + grew + "，并发 " + concurrency + "）");
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

            var entries = audit.Snapshot();
            Assert.AreEqual(1, entries.Count);
            var entry = entries[0];
            Assert.IsTrue(entry.Ok);
            Assert.AreEqual("test.echo", entry.Method);
            Assert.AreEqual("mcp", entry.Source);
            Assert.IsNotNull(entry.ParamsDigest);
            Assert.IsFalse(entry.ParamsDigest.Contains("super-secret-value"));

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(entry);
            Assert.IsFalse(json.Contains("super-secret-value"), "参数原文不得落审计");
            Assert.IsFalse(json.Contains("1234"), "口令原文不得落审计");
            Assert.IsTrue(entry.PinUsed, "携带口令的调用应记 PinUsed=true");
        }

        [TestMethod]
        public void Invoke_WithoutPin_AuditsPinUsedFalse()
        {
            var audit = new MemoryAuditSink();
            var host = BuildHost(r => r.Register("test", SimpleCommand()), audit: audit);
            Assert.IsTrue(host.Invoke(CommandRequest.Create("test.echo")).Ok);
            Assert.IsFalse(audit.Snapshot()[0].PinUsed);
        }

        [TestMethod]
        public void Invoke_Failure_AlsoAudited()
        {
            var audit = new MemoryAuditSink();
            var host = BuildHost(r => r.Register("test", SimpleCommand()), audit: audit);
            Assert.IsFalse(host.Invoke(CommandRequest.Create("test.nope")).Ok);
            var entries = audit.Snapshot();
            Assert.AreEqual(1, entries.Count);
            Assert.IsFalse(entries[0].Ok);
            Assert.AreEqual(CommandErrorCodes.CapabilityNotFound, entries[0].Code);
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
