using System;
using System.Linq;
using System.Windows.Threading;
using CarroDesk.Core;
using CarroDesk.Host.Services;
using CarroDesk.Modules.Awake;
using CarroDesk.Modules.Awake.Models;
using CarroDesk.Modules.Awake.Services;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class AwakeModuleTests
    {
        [TestMethod]
        public void AwakeConfig_Defaults_AreValid()
        {
            var config = AwakeConfig.CreateDefault();
            Assert.IsTrue(config.Enabled);
            Assert.AreEqual(AwakeMode.Passive, config.Mode);
            Assert.IsTrue(config.KeepDisplayOn);
            Assert.AreEqual(30, config.DefaultDurationMinutes);
            Assert.AreEqual("Win+Shift+W", config.Hotkey);
            Assert.IsTrue(config.DisableOnBattery);
            Assert.AreEqual(20, config.BatteryThreshold);
            Assert.AreEqual(120, config.AutoAwakeExitDelaySeconds);
            Assert.IsTrue(config.ProcessLinkEnabled, "智能进程联动默认应启用");
            Assert.IsNotNull(config.CustomPresets);
            Assert.IsTrue(config.CustomPresets.Contains(30));
            Assert.IsNotNull(config.AutoAwakeProcesses);
        }

        [TestMethod]
        public void AwakeModule_Metadata_FollowsModuleContract()
        {
            var module = new AwakeModule();
            Assert.AreEqual("Awake", module.Id);
            Assert.AreEqual(15, module.Order);
            Assert.IsTrue(module.DefaultEnabled);
            Assert.IsFalse(string.IsNullOrWhiteSpace(module.Name));
            Assert.IsFalse(string.IsNullOrWhiteSpace(module.Description));
        }

        [TestMethod]
        public void AwakeModule_GetTrayMenuItems_StrictlyFollowsTwoLevelDesign()
        {
            var module = new AwakeModule();
            var items = module.GetTrayMenuItems()?.ToList();

            Assert.IsNotNull(items);
            Assert.AreEqual(1, items.Count, "AwakeModule 应按照二级收敛规范严格只输出 1 个根菜单项");

            var root = items[0];
            Assert.AreEqual("awake_root", root.Id);
            Assert.IsTrue(root.Header.Contains("保持唤醒"));

            // 检查核心二级子菜单结构
            Assert.IsTrue(root.Children.Any(c => c.Id == "awake_mode_passive"), "应包含关闭选项");
            Assert.IsTrue(root.Children.Any(c => c.Id == "awake_mode_indefinite"), "应包含无限期保持唤醒选项");
            Assert.IsTrue(root.Children.Any(c => c.Id == "awake_mode_timed_root"), "应包含定时保持唤醒子菜单");
            Assert.IsTrue(root.Children.Any(c => c.Id == "awake_mode_until_root"), "应包含保持至指定时刻子菜单");
            Assert.IsTrue(root.Children.Any(c => c.Id == "awake_keep_display"), "应包含保持屏幕常亮选项");
            Assert.IsTrue(root.Children.Any(c => c.Id == "awake_process_link"), "应包含智能进程联动开关");
            Assert.IsTrue(root.Children.Any(c => c.Id == "awake_settings"), "应包含设置选项");

            // 检查定时子菜单预设
            var timedNode = root.Children.First(c => c.Id == "awake_mode_timed_root");
            Assert.IsTrue(timedNode.Children.Count >= 4, "定时子菜单应包含多个预设时长");
            Assert.IsTrue(timedNode.Children.Any(c => c.Id == "awake_timed_15"));
            Assert.IsTrue(timedNode.Children.Any(c => c.Id == "awake_timed_30"));
            Assert.IsTrue(timedNode.Children.Any(c => c.Id == "awake_timed_60"));
            Assert.IsTrue(timedNode.Children.Any(c => c.Id == "awake_timed_custom"), "定时子菜单应包含自定义分钟数项");
        }

        [TestMethod]
        public void AwakeService_ModeTransitions_WorkAsExpected()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var service = new AwakeService(dispatcher, null);
            service.Initialize(AwakeConfig.CreateDefault());

            Assert.AreEqual(AwakeMode.Passive, service.Mode);
            Assert.IsFalse(service.IsActive);

            // 切换为无限期
            service.SetIndefinite();
            Assert.AreEqual(AwakeMode.Indefinite, service.Mode);
            Assert.IsTrue(service.IsActive);
            Assert.AreEqual(TimeSpan.Zero, service.RemainingTime);

            // 切换为定时
            service.SetTimed(45);
            Assert.AreEqual(AwakeMode.Timed, service.Mode);
            Assert.IsTrue(service.IsActive);
            Assert.IsTrue(service.RemainingTime.TotalMinutes > 44 && service.RemainingTime.TotalMinutes <= 45);

            // 切换为屏幕开关
            service.SetKeepDisplayOn(false);
            Assert.IsFalse(service.KeepDisplayOn);
            service.ToggleKeepDisplayOn();
            Assert.IsTrue(service.KeepDisplayOn);

            // 切换为关闭
            service.SetPassive();
            Assert.AreEqual(AwakeMode.Passive, service.Mode);
            Assert.IsFalse(service.IsActive);

            service.Dispose();
        }

        [TestMethod]
        public void ConfigManager_AwakeConfig_SerializationRoundTrip()
        {
            var configService = new ConfigService();
            var manager = new ConfigManager(configService);

            var originalConfig = new AwakeConfig
            {
                KeepDisplayOn = false,
                DefaultDurationMinutes = 60,
                Hotkey = "Ctrl+Shift+F9",
                DisableOnBattery = true,
                BatteryThreshold = 30,
                AutoAwakeExitDelaySeconds = 90,
                AutoAwakeProcesses = new System.Collections.Generic.List<string> { "blender", "ffmpeg" }
            };

            manager.SaveModuleConfig("Awake", originalConfig);
            Assert.IsNotNull(configService.GetModuleToken("Awake"));

            var loadedConfig = manager.GetModuleConfig<AwakeConfig>("Awake");
            Assert.IsNotNull(loadedConfig);
            Assert.AreEqual(false, loadedConfig.KeepDisplayOn);
            Assert.AreEqual(60, loadedConfig.DefaultDurationMinutes);
            Assert.AreEqual("Ctrl+Shift+F9", loadedConfig.Hotkey);
            Assert.AreEqual(30, loadedConfig.BatteryThreshold);
            Assert.AreEqual(90, loadedConfig.AutoAwakeExitDelaySeconds);
            Assert.AreEqual(2, loadedConfig.AutoAwakeProcesses.Count);
            Assert.AreEqual("blender", loadedConfig.AutoAwakeProcesses[0]);
            Assert.AreEqual("ffmpeg", loadedConfig.AutoAwakeProcesses[1]);
        }

        /// <summary>
        /// P1-9：守护进程运行期间用户手动"关闭"不得被自动联动反转。
        /// 直接反射调用私有的 CheckProcessTriggers，避免依赖 1 秒定时器，测试更快也更确定。
        /// </summary>
        [TestMethod]
        public void Awake_UserDisabledWhileGuardProcessRunning_IsNotAutoReverted()
        {
            // 用当前测试进程自身作为"守护进程正在运行"的来源，保证一定存在
            string selfProcess = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

            var config = AwakeConfig.CreateDefault();
            config.AutoAwakeProcesses = new System.Collections.Generic.List<string> { selfProcess };
            config.AutoAwakeExitDelaySeconds = 0;

            var service = new AwakeService(Dispatcher.CurrentDispatcher, null);
            service.Initialize(config);

            InvokeCheckProcessTriggers(service);
            Assert.IsTrue(service.IsProcessTriggered,
                "守护进程正在运行时应自动触发进程联动");
            Assert.IsTrue(service.IsActive,
                "守护进程正在运行时应保持唤醒生效");
            Assert.AreEqual(AwakeMode.Passive, service.Mode,
                "进程联动独立运行，不应篡改用户配置模式");

            service.SetPassiveByUser();
            Assert.AreEqual(AwakeMode.Passive, service.Mode);
            Assert.IsFalse(service.IsProcessTriggered);
            Assert.IsFalse(service.IsActive);

            // 关键断言：下一个轮询周期不得把用户的"关闭"反转回保持唤醒
            InvokeCheckProcessTriggers(service);
            Assert.AreEqual(AwakeMode.Passive, service.Mode,
                "用户的关闭操作被守护进程联动自动反转了");
            Assert.IsFalse(service.IsProcessTriggered);
            Assert.IsFalse(service.IsActive);

            service.Dispose();
        }

        /// <summary>
        /// 智能进程联动总开关：关闭后即使目标进程正在运行也不得触发联动；
        /// 重新启用后应立即恢复联动能力。
        /// </summary>
        [TestMethod]
        public void Awake_ProcessLinkDisabled_SuppressesAutoTrigger()
        {
            string selfProcess = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

            var config = AwakeConfig.CreateDefault();
            config.AutoAwakeProcesses = new System.Collections.Generic.List<string> { selfProcess };
            config.AutoAwakeExitDelaySeconds = 0;
            config.ProcessLinkEnabled = false;

            var service = new AwakeService(Dispatcher.CurrentDispatcher, null);
            service.Initialize(config);

            Assert.IsFalse(service.IsProcessLinkEnabled);
            InvokeCheckProcessTriggers(service);
            Assert.AreEqual(AwakeMode.Passive, service.Mode,
                "总开关关闭时不得自动开启保持唤醒");
            Assert.IsFalse(service.IsProcessTriggered);
            Assert.IsFalse(service.IsActive);

            // 重新启用后联动应恢复
            config.ProcessLinkEnabled = true;
            service.UpdateConfig(config);
            Assert.IsTrue(service.IsProcessLinkEnabled);

            InvokeCheckProcessTriggers(service);
            Assert.IsTrue(service.IsProcessTriggered,
                "重新启用后应恢复自动联动");
            Assert.IsTrue(service.IsActive,
                "重新启用后应保持唤醒生效");
            Assert.AreEqual(AwakeMode.Passive, service.Mode,
                "进程联动不改变用户模式");

            service.Dispose();
        }

        /// <summary>
        /// 联动进行中关闭总开关：必须立即撤销保持唤醒，不得留下"已关闭却仍在唤醒"的残留状态。
        /// </summary>
        [TestMethod]
        public void Awake_DisablingProcessLink_RevokesActiveLink()
        {
            string selfProcess = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

            var config = AwakeConfig.CreateDefault();
            config.AutoAwakeProcesses = new System.Collections.Generic.List<string> { selfProcess };
            config.AutoAwakeExitDelaySeconds = 0;

            var service = new AwakeService(Dispatcher.CurrentDispatcher, null);
            service.Initialize(config);

            InvokeCheckProcessTriggers(service);
            Assert.IsTrue(service.IsProcessTriggered);
            Assert.IsTrue(service.IsActive);
            Assert.AreEqual(AwakeMode.Passive, service.Mode);

            config.ProcessLinkEnabled = false;
            service.UpdateConfig(config);

            Assert.IsFalse(service.IsProcessTriggered, "关闭开关应清除进程联动状态");
            Assert.AreEqual(AwakeMode.Passive, service.Mode, "关闭开关后用户模式仍为 Passive");
            Assert.IsFalse(service.IsActive, "关闭开关应撤销联动触发的保持唤醒");

            // 后续轮询不得把状态反转回来
            InvokeCheckProcessTriggers(service);
            Assert.AreEqual(AwakeMode.Passive, service.Mode);
            Assert.IsFalse(service.IsActive);

            service.Dispose();
        }

        /// <summary>
        /// 用户手动开启永久模式后，进程联动不污染 Mode，进程退出后依然保持用户设置的永久模式。
        /// </summary>
        [TestMethod]
        public void Awake_ProcessLink_DoesNotPolluteManualIndefiniteMode()
        {
            string selfProcess = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

            var config = AwakeConfig.CreateDefault();
            config.AutoAwakeProcesses = new System.Collections.Generic.List<string> { selfProcess };
            config.AutoAwakeExitDelaySeconds = 0;

            var service = new AwakeService(Dispatcher.CurrentDispatcher, null);
            service.Initialize(config);

            // 用户手动开启永久
            service.SetIndefinite();
            Assert.AreEqual(AwakeMode.Indefinite, service.Mode);
            Assert.IsTrue(service.IsActive);

            // 检测到进程
            InvokeCheckProcessTriggers(service);
            Assert.IsTrue(service.IsProcessTriggered);
            Assert.AreEqual(AwakeMode.Indefinite, service.Mode, "用户手动模式不得被进程联动篡改");

            // 目标进程解除 (清空名单模拟所有进程退出)
            config.AutoAwakeProcesses = new System.Collections.Generic.List<string>();
            service.UpdateConfig(config);

            Assert.IsFalse(service.IsProcessTriggered, "目标移除后联动状态应清除");
            Assert.AreEqual(AwakeMode.Indefinite, service.Mode, "进程退出后用户的 Indefinite 模式必须保留");
            Assert.IsTrue(service.IsActive, "用户的 Indefinite 模式仍应保持唤醒生效");

            service.Dispose();
        }

        /// <summary>
        /// 目标进程退出时，若设置了退出缓冲时间，应进入缓冲等待状态且 IsProcessExiting 为 true。
        /// </summary>
        [TestMethod]
        public void Awake_ProcessExitDelay_BufferingState()
        {
            string selfProcess = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

            var config = AwakeConfig.CreateDefault();
            config.AutoAwakeProcesses = new System.Collections.Generic.List<string> { selfProcess };
            config.AutoAwakeExitDelaySeconds = 60;

            var service = new AwakeService(Dispatcher.CurrentDispatcher, null);
            service.Initialize(config);

            // 触发联动
            InvokeCheckProcessTriggers(service);
            Assert.IsTrue(service.IsProcessTriggered);
            Assert.IsFalse(service.IsProcessExiting);
            Assert.IsTrue(service.IsActive);

            // 切换为不存在的进程以模拟退出
            config.AutoAwakeProcesses = new System.Collections.Generic.List<string> { "non_existent_proc_test_xyz" };
            service.UpdateConfig(config);

            InvokeCheckProcessTriggers(service);
            Assert.IsTrue(service.IsProcessTriggered, "缓冲期间保持唤醒仍生效");
            Assert.IsTrue(service.IsProcessExiting, "应处于退出缓冲状态");
            Assert.AreEqual(60, service.ProcessExitPendingSeconds);
            Assert.IsTrue(service.IsActive);

            service.Dispose();
        }

        private static void InvokeCheckProcessTriggers(AwakeService service)
        {
            var method = typeof(AwakeService).GetMethod(
                "CheckProcessTriggers",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(method, "未找到 CheckProcessTriggers，测试需随实现同步更新");
            method.Invoke(service, null);
        }
    }
}
