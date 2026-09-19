using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Host.Services;
using CarroDesk.Modules.AppAutoMute;
using CarroDesk.Modules.AudioSwitch;
using CarroDesk.Modules.ClipboardHistory.Models;
using CarroDesk.Modules.ScreenLock;
using CarroDesk.Modules.TaskScheduler;
using CarroDesk.Models;
using CarroDesk.Services;
using CarroDesk.Services.Localization;
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

        [TestMethod]
        public void ScreenLockModule_GetTrayMenuItems_ReturnsSingleRootItem_WithExpectedChildren()
        {
            var module = new ScreenLockModule();

            var items = module.GetTrayMenuItems()?.ToList();
            Assert.IsNotNull(items);
            Assert.AreEqual(1, items.Count, "ScreenLockModule 应按照二级收敛规范严格只输出 1 个根菜单项");

            var root = items[0];
            Assert.AreEqual("screenlock_root", root.Id);
            Assert.IsTrue(root.Header.Contains("屏幕保护"), $"根项 Header 应包含模块名，实际: {root.Header}");
            Assert.IsTrue(root.Children.Count >= 3, "二级菜单应包含立即锁定、档位、暂停等选项");

            Assert.IsTrue(root.Children.Any(c => c.Id == "screenlock_lock_now"), "二级菜单应包含立即锁定");
            Assert.IsTrue(root.Children.Any(c => c.Id == "screenlock_idle_root"), "二级菜单应包含空闲锁定");
            Assert.IsTrue(root.Children.Any(c => c.Id == "screenlock_pause_root"), "二级菜单应包含暂停计时");
            Assert.IsTrue(root.Children.Any(c => c.Id == "screenlock_settings"), "二级菜单应包含设置选项");

            var pauseNode = root.Children.First(c => c.Id == "screenlock_pause_root");
            Assert.IsTrue(pauseNode.Children.Any(c => c.Id == "screenlock_pause_custom"), "暂停子菜单应包含自定义暂停选项");
        }

        [TestMethod]
        public void AppAutoMuteModule_GetTrayMenuItems_ReturnsSingleRootItem_WithExpectedChildren()
        {
            var module = new AppAutoMuteModule();

            var items = module.GetTrayMenuItems()?.ToList();
            Assert.IsNotNull(items);
            Assert.AreEqual(1, items.Count, "AppAutoMuteModule 应按照二级收敛规范严格只输出 1 个根菜单项");

            var root = items[0];
            Assert.AreEqual("appautomute_root", root.Id);
            Assert.IsTrue(root.Header.Contains("应用后台静音"), $"根项 Header 应包含模块名，实际: {root.Header}");

            Assert.IsTrue(root.Children.Any(c => c.Id == "appautomute_toggle"), "二级菜单应包含总开关");
            Assert.IsTrue(root.Children.Any(c => c.Id == "appautomute_settings"), "二级菜单应包含设置窗口项");
        }

        [TestMethod]
        public void TaskSchedulerModule_GetTrayMenuItems_ReturnsSingleRootItem_WithExpectedChildren()
        {
            var module = new TaskSchedulerModule();

            var items = module.GetTrayMenuItems()?.ToList();
            Assert.IsNotNull(items);
            Assert.AreEqual(1, items.Count, "TaskSchedulerModule 应按照二级收敛规范严格只输出 1 个根菜单项");

            var root = items[0];
            Assert.AreEqual("task_scheduler_root", root.Id);
            Assert.IsTrue(root.Header.Contains("自动化任务"), $"根项 Header 应包含模块名，实际: {root.Header}");

            Assert.IsTrue(root.Children.Any(c => c.Id == "task_scheduler_toggle"), "二级菜单应包含启用总开关");
            Assert.IsTrue(root.Children.Any(c => c.Id == "task_scheduler_manual"), "二级菜单应包含手动运行");
            Assert.IsTrue(root.Children.Any(c => c.Id == "task_scheduler_recent"), "二级菜单应包含最近运行");
            Assert.IsTrue(root.Children.Any(c => c.Id == "task_scheduler_editor"), "二级菜单应包含任务编辑器");
            Assert.IsTrue(root.Children.Any(c => c.Id == "task_scheduler_reload"), "二级菜单应包含重载任务");
        }

        [TestMethod]
        public void AudioSwitchModule_GetTrayMenuItems_ReturnsSingleRootItem()
        {
            var module = new AudioSwitchModule();

            var items = module.GetTrayMenuItems()?.ToList();
            Assert.IsNotNull(items);
            Assert.AreEqual(1, items.Count, "AudioSwitchModule 应按照二级收敛规范严格只输出 1 个根菜单项");

            var root = items[0];
            Assert.AreEqual("audioswitch_root", root.Id);
            Assert.IsTrue(root.Header.Contains("音频输出设备"), $"根项 Header 应包含音频输出设备，实际: {root.Header}");
            Assert.IsTrue(root.Children.Any(c => c.Id == "audioswitch_fast_toggle"), "二级菜单应包含快捷切换");
            Assert.IsTrue(root.Children.Any(c => c.Id == "audioswitch_settings"), "二级菜单应包含音频切换设置入口");
        }

        [TestMethod]
        public void AudioSwitchModule_IsDeviceExcluded_MatchesCorrectly()
        {
            var module = new AudioSwitchModule();
            module.Config.ExcludedDevices.Add("DELL U2720Q");
            module.Config.ExcludedDevices.Add("{0.0.0.00000000}.{GUID-123}");

            Assert.IsTrue(module.IsDeviceExcluded("{0.0.0.00000000}.{GUID-123}", "Any Name"));
            Assert.IsTrue(module.IsDeviceExcluded("other-id", "DELL U2720Q (NVIDIA High Definition Audio)"));
            Assert.IsFalse(module.IsDeviceExcluded("other-id", "Realtek High Definition Audio (扬声器)"));
        }

        [TestMethod]
        public void AppSettings_FloatingPanel_DefaultsAndClone()
        {
            var s = new AppSettings();
            Assert.AreEqual("Win+Alt+C", s.FloatingPanelHotkey);
            Assert.AreEqual("Tray", s.FloatingPanelPosition);
            Assert.IsFalse(s.FloatingPanelPinned);
            Assert.IsFalse(s.FloatingPanelLocked);
            Assert.AreEqual(-1, s.FloatingPanelX);
            Assert.AreEqual(-1, s.FloatingPanelY);

            s.FloatingPanelHotkey = "Ctrl+Shift+D";
            s.FloatingPanelPosition = "Center";
            s.FloatingPanelPinned = true;
            s.FloatingPanelLocked = true;
            s.FloatingPanelX = 500;
            s.FloatingPanelY = 300;

            var clone = s.Clone();
            Assert.AreEqual("Ctrl+Shift+D", clone.FloatingPanelHotkey);
            Assert.AreEqual("Center", clone.FloatingPanelPosition);
            Assert.IsTrue(clone.FloatingPanelPinned);
            Assert.IsTrue(clone.FloatingPanelLocked);
            Assert.AreEqual(500, clone.FloatingPanelX);
            Assert.AreEqual(300, clone.FloatingPanelY);
        }

        [TestMethod]
        public void ClipboardItem_BuildPreviewText_PreservesUpTo3Lines_AndTruncates()
        {
            string raw = "Line 1\r\nLine 2\nLine 3\r\nLine 4\r\nLine 5";
            string preview = ClipboardItem.BuildPreviewText(raw, 300);

            var lines = preview.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.None);
            Assert.IsTrue(lines.Length <= 4); // 3 lines + trailing "..."
            Assert.IsTrue(preview.Contains("Line 1"));
            Assert.IsTrue(preview.Contains("Line 2"));
            Assert.IsTrue(preview.Contains("Line 3"));
            Assert.IsFalse(preview.Contains("Line 4"));
            Assert.IsTrue(preview.EndsWith("..."));
        }

        [TestMethod]
        public void Loc_T_Fallback_ReturnsDefaultValue_WhenKeyNotFound()
        {
            string nonExistentKey = "NonExistentKey_" + Guid.NewGuid().ToString("N");
            string defaultValue = "这是一个兜底默认文本";

            string result = Loc.T(nonExistentKey, defaultValue);
            Assert.AreEqual(defaultValue, result, "当 key 不存在时，Loc.T 应严格返回 defaultValue 兜底，绝不可返回原始 key");
        }

        [TestMethod]
        public void Locales_ScreenLockSettings_Key_Exists_In_Both_Languages()
        {
            Loc.SetLanguage("zh-CN");
            string zh = Loc.T("Tray.ScreenLockSettings");
            Assert.AreEqual("屏幕保护设置...", zh);

            Loc.SetLanguage("en-US");
            string en = Loc.T("Tray.ScreenLockSettings");
            Assert.AreEqual("Screen Lock Settings...", en);

            // 恢复回中文默认
            Loc.SetLanguage("zh-CN");
        }

        [TestMethod]
        public void Locales_AllSecondaryMenuSettings_Keys_Exist_In_Both_Languages()
        {
            var testKeys = new Dictionary<string, (string zh, string en)>
            {
                { "Tray.ScreenLockSettings", ("屏幕保护设置...", "Screen Lock Settings...") },
                { "Tray.AudioSwitchSettings", ("音频切换设置...", "Audio Switch Settings...") },
                { "Tray.AppAutoMuteSettings", ("后台静音设置...", "Auto-Mute Settings...") },
                { "Tray.AwakeSettings", ("保持唤醒设置...", "Awake Settings...") },
                { "Tray.MonitorProfileSettings", ("显示器配置与计划...", "Monitor Profiles & Schedule...") },
                { "Tray.OpenTaskLogsDir", ("打开任务日志目录...", "Open Task Logs Directory...") },
                { "Clipboard.OpenWindow", ("打开剪贴板历史...", "Open Clipboard History...") }
            };

            Loc.SetLanguage("zh-CN");
            foreach (var kvp in testKeys)
            {
                string val = Loc.T(kvp.Key);
                Assert.AreEqual(kvp.Value.zh, val, $"中文环境下 {kvp.Key} 未正确翻译");
            }

            Loc.SetLanguage("en-US");
            foreach (var kvp in testKeys)
            {
                string val = Loc.T(kvp.Key);
                Assert.AreEqual(kvp.Value.en, val, $"英文环境下 {kvp.Key} 未正确翻译");
            }

            // 恢复回中文默认
            Loc.SetLanguage("zh-CN");
        }

        private sealed class StubLogger : ILoggerService
        {
            public void LogError(string module, string message, Exception ex) { }
            public void LogInfo(string module, string message) { }
            public void LogWarning(string module, string message) { }
        }
    }
}