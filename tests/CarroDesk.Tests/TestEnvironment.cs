using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 测试进程级环境隔离与共享 UI 测试辅助。
    ///
    /// 背景一（数据隔离）：<see cref="ConfigService"/> 的数据目录默认指向 %AppData%\CarroDesk，
    /// 而多个测试会直接 new ConfigService() 并落盘。若不隔离，运行一次测试就会
    /// 覆写开发者/用户真实的 config.json（已实测复现）。这里把数据目录重定向到
    /// 本次运行专属的临时目录，既有测试无需逐个改造。
    ///
    /// 背景二（UI 测试共享状态）：各 UI 测试类原先各自复制了一份 RunInSta，
    /// 其中"关闭最后一个窗口"会触发 <c>Application.Shutdown()</c>（WPF 默认
    /// ShutdownMode 为 OnLastWindowClose），只要后续代码处理了调度队列，
    /// 之后所有 UI 测试都会以"应用程序对象正在关闭"失败。这里统一为
    /// OnExplicitShutdown，并提供共享的 STA 执行器与消息泵。
    /// </summary>
    [TestClass]
    public class TestEnvironment
    {
        /// <summary>本次测试运行专属的数据目录（其余测试如需落盘应以此为根）。</summary>
        public static string TempRoot { get; private set; }

        [AssemblyInitialize]
        public static void AssemblyInit(TestContext context)
        {
            TempRoot = Path.Combine(
                Path.GetTempPath(),
                "CarroDesk.Tests",
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            Directory.CreateDirectory(TempRoot);
            ConfigService.DataDirOverride = TempRoot;

            context.WriteLine("测试数据目录已隔离至: " + TempRoot);
        }

        [AssemblyCleanup]
        public static void AssemblyCleanup()
        {
            ConfigService.DataDirOverride = null;

            try
            {
                if (!string.IsNullOrEmpty(TempRoot) && Directory.Exists(TempRoot))
                {
                    Directory.Delete(TempRoot, true);
                }
            }
            catch
            {
                // 临时目录清理失败不影响测试结论（残留目录由系统回收）
            }
        }

        /// <summary>
        /// 确保存在一个不会自行关闭的 WPF Application。
        /// 必须显式设置 ShutdownMode，否则关闭最后一个窗口会关停整个 Application，
        /// 使同一进程内后续所有 UI 测试失败。
        ///
        /// 注意：Application 是线程亲和的（归创建它的 Dispatcher 所有），
        /// 后续测试运行在不同的 STA 线程上，此时只能复用其实例，不能读写它的属性。
        /// </summary>
        public static void EnsureApplication()
        {
            var current = Application.Current;
            if (current == null)
            {
                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }
            else if (current.Dispatcher.CheckAccess())
            {
                current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            CarroDesk.Services.Localization.I18nService.Instance.Init("zh-CN");
        }

        /// <summary>在独立的 STA 线程上执行 UI 操作，并回传播出的异常。</summary>
        public static void RunInSta(Action action)
        {
            Exception captured = null;
            var thread = new Thread(() =>
            {
                try
                {
                    EnsureApplication();
                    action();
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (captured != null)
            {
                throw new Exception("STA UI 线程异常: " + captured.Message, captured);
            }
        }

        /// <summary>处理当前线程调度队列中的挂起项（不阻塞等待）。</summary>
        public static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                (Action)(() => { frame.Continue = false; }));
            Dispatcher.PushFrame(frame);
        }
    }
}
