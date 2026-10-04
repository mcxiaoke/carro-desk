using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CarroDesk.Core;
using CarroDesk.Core.Commands;
using CarroDesk.Host.Commands;
using CarroDesk.Host.Services;
using CarroDesk.Modules.Awake;
using CarroDesk.Modules.Awake.Services;
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
                new[] { "awake.status", "awake.on", "awake.off", "awake.sleep", "awake.sleep.cancel", "awake.shutdown", "awake.shutdown.cancel" },
                commands.Select(c => c.Name).ToList());
            Assert.AreEqual(CommandRisk.ReadOnly, commands.First(c => c.Name == "awake.status").Risk);
            Assert.AreEqual(CommandRisk.Low, commands.First(c => c.Name == "awake.on").Risk);
            Assert.AreEqual(CommandRisk.Low, commands.First(c => c.Name == "awake.off").Risk);
            Assert.AreEqual(CommandRisk.Low, commands.First(c => c.Name == "awake.sleep").Risk);
            Assert.AreEqual(CommandRisk.Privileged, commands.First(c => c.Name == "awake.shutdown").Risk);
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

        [TestMethod]
        public void Invoke_Sleep_DefaultThirtySeconds_AndScheduledInStatus()
        {
            var module = CreateRunningModule();
            var host = BuildHost(module);

            var status = AsJObject(host.Invoke(CommandRequest.Create("awake.sleep")));

            Assert.AreEqual("sleep", (string)status["pendingAction"]);
            Assert.AreEqual(JTokenType.Integer, status["pendingSecondsRemaining"].Type);
            int remaining = (int)status["pendingSecondsRemaining"];
            Assert.IsTrue(remaining >= 25 && remaining <= 30, "default 30s, got " + remaining);
            Assert.IsNotNull(status["pendingFireAt"]);
        }

        [TestMethod]
        public void Invoke_Sleep_OutOfRangeSeconds_Rejected()
        {
            var module = CreateRunningModule();
            var host = BuildHost(module);

            foreach (var bad in new[] { -1, 86401 })
            {
                var request = CommandRequest.Create("awake.sleep");
                request.Params["seconds"] = bad;
                var result = host.Invoke(request);
                Assert.IsFalse(result.Ok, "seconds=" + bad);
                Assert.AreEqual(CommandErrorCodes.InvalidParams, result.Error.Code, "seconds=" + bad);
            }
        }

        [TestMethod]
        public void Invoke_Shutdown_DefaultThirtySeconds_AndScheduledInStatus()
        {
            var module = CreateRunningModule();
            var host = BuildHost(module);

            var status = AsJObject(host.Invoke(CommandRequest.Create("awake.shutdown")));

            Assert.AreEqual("shutdown", (string)status["pendingAction"]);
            int remaining = (int)status["pendingSecondsRemaining"];
            Assert.IsTrue(remaining >= 25 && remaining <= 30, "default 30s, got " + remaining);
        }

        [TestMethod]
        public void Invoke_SleepThenShutdown_NewScheduleReplacesOld()
        {
            var module = CreateRunningModule();
            var host = BuildHost(module);

            Assert.IsTrue(host.Invoke(CommandRequest.Create("awake.sleep")).Ok);
            var request = CommandRequest.Create("awake.shutdown");
            request.Params["seconds"] = 60;
            var status = AsJObject(host.Invoke(request));

            // 单一待执行槽位：新调度替换旧调度，避免睡醒后过期关机定时器立即触发
            Assert.AreEqual("shutdown", (string)status["pendingAction"]);
        }

        [TestMethod]
        public void Invoke_SleepCancel_ClearsPending()
        {
            var module = CreateRunningModule();
            var host = BuildHost(module);

            Assert.IsTrue(host.Invoke(CommandRequest.Create("awake.sleep")).Ok);
            var status = AsJObject(host.Invoke(CommandRequest.Create("awake.sleep.cancel")));

            Assert.AreEqual(JTokenType.Null, status["pendingAction"].Type);
            Assert.AreEqual(JTokenType.Null, status["pendingSecondsRemaining"].Type);
        }

        [TestMethod]
        public void Invoke_ShutdownCancel_ClearsPending()
        {
            var module = CreateRunningModule();
            var host = BuildHost(module);

            Assert.IsTrue(host.Invoke(CommandRequest.Create("awake.shutdown")).Ok);
            var status = AsJObject(host.Invoke(CommandRequest.Create("awake.shutdown.cancel")));

            Assert.AreEqual(JTokenType.Null, status["pendingAction"].Type);
        }

        [TestMethod]
        public void Invoke_CancelWithoutPending_IsNoOpSuccess()
        {
            var module = CreateRunningModule();
            var host = BuildHost(module);

            var result = host.Invoke(CommandRequest.Create("awake.shutdown.cancel"));
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(JTokenType.Null, ((JObject)JToken.FromObject(result.Data))["pendingAction"].Type);
        }

        [TestMethod]
        public void Invoke_Off_DoesNotTouchPendingPowerAction()
        {
            var module = CreateRunningModule();
            var host = BuildHost(module);

            Assert.IsTrue(host.Invoke(CommandRequest.Create("awake.shutdown")).Ok);
            var status = AsJObject(host.Invoke(CommandRequest.Create("awake.off")));

            // awake.off 管保持唤醒，不牵连电源动作调度
            Assert.AreEqual("shutdown", (string)status["pendingAction"]);
        }

        /// <summary>
        /// 睡眠路径必须在调用 SetSuspendState 之前启用 SE_SHUTDOWN_NAME 特权。
        ///
        /// SetSuspendState 需要"关机"特权，默认交互式登录进程并不持有；受限账户 /
        /// UAC 提升后的非管理员账户下会返回 FALSE(1314 权限不足)，而 IPC 早已回"已调度"，
        /// 用户只在到点后才发现根本没睡下去。（关机路径借用 shutdown.exe，不受影响。）
        ///
        /// 断言：AwakeService 存在无参的 TryEnableShutdownPrivilege(out string)，
        /// 且 ExecutePowerAction 的睡眠分支在调用 SetSuspendState 之前调用它。
        /// 这里不真正睡眠 —— 会挂起测试机器。
        /// </summary>
        [TestMethod]
        public void SleepPath_EnablesShutdownPrivilegeBeforeSuspend()
        {
            var serviceType = typeof(AwakeService);

            var tryEnable = serviceType.GetMethod("TryEnableShutdownPrivilege",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            Assert.IsNotNull(tryEnable, "AwakeService 必须提供启用 SE_SHUTDOWN_NAME 的入口");

            var parameters = tryEnable.GetParameters();
            Assert.AreEqual(1, parameters.Length);
            Assert.AreEqual(typeof(string).MakeByRefType(), parameters[0].ParameterType,
                "应通过 out string 回传失败原因");

            // 直接调用一次确认 P/Invoke 结构与错误处理不抛异常（不会触发任何电源动作）
            object[] args = new object[] { null };
            bool ok = (bool)tryEnable.Invoke(null, args);
            Assert.IsTrue(ok || !string.IsNullOrEmpty((string)args[0]),
                "特权启用失败时必须给出错误原因，不能静默");

            // 顺序校验：ExecutePowerAction 里必须先启用特权再调用 SetSuspendState
            string source = ReadAwakeServiceSource();
            int body = source.IndexOf("private void ExecutePowerAction", StringComparison.Ordinal);
            Assert.IsTrue(body >= 0, "应能找到 ExecutePowerAction");
            int enableAt = source.IndexOf("TryEnableShutdownPrivilege", body, StringComparison.Ordinal);
            int suspendAt = source.IndexOf("SetSuspendState(", body, StringComparison.Ordinal);
            Assert.IsTrue(enableAt > body && suspendAt > body, "ExecutePowerAction 中应同时出现特权启用与 SetSuspendState");
            Assert.IsTrue(enableAt < suspendAt, "必须先启用 SE_SHUTDOWN_NAME 再调用 SetSuspendState");
        }

        private static string ReadAwakeServiceSource()
        {
            string name = "AwakeService.cs";
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CarroDesk.slnx")))
            {
                dir = dir.Parent;
            }
            Assert.IsNotNull(dir, "未能定位仓库根目录（向上找不到 CarroDesk.slnx）");

            string path = Path.Combine(dir.FullName, "src", "Modules", "Awake", "Services", name);
            Assert.IsTrue(File.Exists(path), "未找到源文件: " + path);
            return File.ReadAllText(path, Encoding.UTF8);
        }
    }
}
