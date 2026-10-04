using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Host.Services;
using CarroDesk.Modules.AudioSwitch;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 音频设备变化刷新（R9）。
    ///
    /// 背景：<c>IAudioService.DevicesChanged</c> 的契约注释承诺"WM_DEVICECHANGE 桥接"，
    /// 但 <c>RaiseDevicesChanged()</c> 全仓只有定义、零调用点，
    /// AudioSwitchModule 的订阅/退订全是死代码 —— 插拔耳机后托盘仍显示旧设备名，必须重启进程。
    ///
    /// 这里不真插拔设备，而是验证"刷新链路真的被接上了"：订阅方回调一次，
    /// 模块必须刷新当前设备并请求托盘刷新。
    /// </summary>
    [TestClass]
    public class AudioDeviceChangeTests
    {
        private sealed class StubAudioService : IAudioService
        {
            public AudioDeviceItem Default { get; set; } = new AudioDeviceItem { Id = "dev-1", Name = " Speakers " };
            public List<AudioDeviceItem> Playback { get; set; } = new List<AudioDeviceItem>();
            public int RefreshCount { get; private set; }

            public List<AudioDeviceItem> GetPlaybackDevices() { return Playback; }

            public AudioDeviceItem GetDefaultPlaybackDevice()
            {
                RefreshCount++;
                return Default;
            }

            public AudioDeviceItem FindDeviceByPattern(string pattern) { return null; }
            public bool SetDefaultPlaybackDevice(string deviceId) { return false; }
            public bool SetProcessMute(string processName, bool mute) { return false; }
            public void UnmuteProcesses(IEnumerable<string> processNames) { }
            public List<string> GetActiveAudioProcesses() { return new List<string>(); }
            public void Dispose() { }

            public event Action DevicesChanged;

            /// <summary>模拟一次设备变化通知（等价于 WM_DEVICECHANGE 桥接后的行为）。</summary>
            public void SimulateDeviceChange(AudioDeviceItem newDefault)
            {
                Default = newDefault;
                DevicesChanged?.Invoke();
            }
        }

        /// <summary>最小可用模块上下文：只提供 AudioService，其余服务返回 null。</summary>
        private sealed class StubModuleContext : IModuleContext
        {
            private readonly IAudioService _audio;

            public StubModuleContext(IAudioService audio)
            {
                _audio = audio;
            }

            public string ModuleId { get { return "AudioSwitch"; } }
            public Dispatcher Dispatcher { get { return null; } }
            public T GetService<T>() where T : class
            {
                return typeof(T) == typeof(IAudioService) ? (T)_audio : null;
            }
            public void RequestTrayRefresh() { }
            public void ShowNotification(string message, string title = "CarroDesk") { }
        }

        [TestMethod]
        public void AudioSwitchModule_RefreshesDevice_WhenServiceRaisesDevicesChanged()
        {
            var stub = new StubAudioService();
            var module = new AudioSwitchModule();

            // ModuleBase.Initialize 会拉取模块配置；这里注入只提供 AudioService 的最小上下文
            module.Initialize(new StubModuleContext(stub));
            module.Start();

            try
            {
                Assert.AreEqual("dev-1", module.CurrentDefaultDevice?.Id, "启动后应已读取默认设备");
                int before = stub.RefreshCount;

                stub.SimulateDeviceChange(new AudioDeviceItem { Id = "dev-2", Name = " Headphones " });

                Assert.AreEqual(1, stub.RefreshCount - before, "设备变化后必须重新查询默认端点");
                Assert.AreEqual("dev-2", module.CurrentDefaultDevice?.Id,
                    "模块应反映新的默认设备（托盘标题/ToolTip 由此重建）");
            }
            finally
            {
                module.Stop();
            }
        }

        /// <summary>接口必须保留显式触发入口，否则宿主/测试无法驱动刷新链路。</summary>
        [TestMethod]
        public void AudioService_ExposesRaiseDevicesChanged()
        {
            var method = typeof(AudioService).GetMethod("RaiseDevicesChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            Assert.IsNotNull(method, "AudioService 应保留 RaiseDevicesChanged() 触发入口");
            Assert.AreEqual(typeof(void), method.ReturnType);
        }

        /// <summary>
        /// 宿主启动时必须真的调用了 StartDeviceNotifications。
        ///
        /// 回归防护：DevicesChanged 的契约注释承诺 WM_DEVICECHANGE 桥接，但实现长期缺失
        /// （RaiseDevicesChanged 零调用点），插拔耳机后托盘仍显示旧设备名。
        /// 断言 App.xaml.cs 里有实际调用点，防止再次退化成死代码。
        /// </summary>
        [TestMethod]
        public void App_StartsAudioDeviceNotifications()
        {
            string source = ReadSourceFile("src", "App.xaml.cs");
            StringAssert.Contains(source, "audioService.StartDeviceNotifications(",
                "App 启动流程必须调用 StartDeviceNotifications，否则设备变化刷新链路不存在");
            StringAssert.Contains(source, "Services.AddSingleton<IAudioService>(audioService)",
                "IAudioService 仍需注册为单例（模块依赖它订阅 DevicesChanged）");
        }

        /// <summary>AudioService 必须实现 IDisposable，才能在宿主退出时释放消息窗口与通知句柄。</summary>
        [TestMethod]
        public void AudioService_IsDisposable_AndReleasesDeviceNotification()
        {
            Assert.IsTrue(typeof(IDisposable).IsAssignableFrom(typeof(AudioService)),
                "AudioService 必须实现 IDisposable：设备通知窗口与 RegisterDeviceNotification 句柄需要显式释放");

            TestEnvironment.RunInSta(() =>
            {
                var service = new AudioService();
                try
                {
                    int raised = 0;
                    service.DevicesChanged += () => raised++;

                    // 必须显式传入本 STA 线程的 Dispatcher：
                    // HwndSource 有线程亲和，而 Application.Current 可能属于另一个
                    // （测试进程中已结束的）STA 线程，那样 Invoke 会永久阻塞。
                    service.StartDeviceNotifications(Dispatcher.CurrentDispatcher);
                    service.StartDeviceNotifications(Dispatcher.CurrentDispatcher);

                    // RaiseDevicesChanged 在默认端点未变化时应被去抖掉（首次采样已建立基线）
                    service.RaiseDevicesChanged();
                    Assert.AreEqual(0, raised, "默认端点未变化时不得广播设备变化");

                    // 停止 + 释放必须幂等且不抛异常
                    service.StopDeviceNotifications();
                    service.StopDeviceNotifications();
                    service.Dispose();
                    service.Dispose();
                }
                finally
                {
                    try { service.Dispose(); } catch { }
                }
            });
        }

        private static string ReadSourceFile(params string[] relativeParts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CarroDesk.slnx")))
            {
                dir = dir.Parent;
            }
            Assert.IsNotNull(dir, "未能定位仓库根目录（向上找不到 CarroDesk.slnx）");

            string path = dir.FullName;
            foreach (var part in relativeParts) path = Path.Combine(path, part);
            Assert.IsTrue(File.Exists(path), "未找到源文件: " + path);
            return File.ReadAllText(path, Encoding.UTF8);
        }
    }
}