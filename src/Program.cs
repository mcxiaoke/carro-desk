using System;
using System.IO;
using System.Text;
using System.Threading;
using CarroDesk.Host.Ipc;
using CarroDesk.Services;

namespace CarroDesk
{
    /// <summary>
    /// 应用程序统一入口点：
    /// 1. 第一时间拦截 CLI 调用（ctl 与 --mcp），毫秒级管道转发/stdio处理，完全不加载 WPF 运行时与 XAML 资源；
    /// 2. 基于数据目录确定性哈希进行单实例互斥检测；
    /// 3. 第二实例毫秒级定向唤醒主实例窗口并返回 ExitCode=2 退出；
    /// 4. 仅首个主实例初始化 WPF 框架并进入 UI 消息循环。
    /// </summary>
    public static class Program
    {
        /// <summary>退出码：已有实例在运行，已定向唤醒既有实例后跳过启动</summary>
        public const int ExitCodeAlreadyRunning = 2;

        private static Mutex _instanceMutex;

        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                // 确保基准目录正确
                Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            }
            catch { /* ignore */ }

            // 1. [CLI 拦截] MCP stdio 瘦进程模式
            if (args != null && args.Length > 0
                && string.Equals(args[0], "--mcp", StringComparison.OrdinalIgnoreCase))
            {
                ParentConsole.TryAttach();
                try { Console.OutputEncoding = Encoding.UTF8; } catch { }
                Environment.ExitCode = McpStdioServer.Run(Console.In, Console.Out, Console.Error, () => new PipeRpcClient());
                return;
            }

            // 2. [CLI 拦截] `ctl` 控制命令行转发（IPC 设计 §5.3，S3）
            if (ControlArgs.IsControlInvocation(args))
            {
                try { Console.OutputEncoding = Encoding.UTF8; } catch { }
                Environment.ExitCode = ForwardControlInvocation(args);
                return;
            }

            // 3. [单实例互斥量保护] 基于数据目录确定性 SHA256 哈希
            var mutexName = ConfigService.InstanceMutexName;
            bool isNew = false;
            try
            {
                // 两阶段创建：首先仅创建/打开内核互斥体句柄（initiallyOwned: false），
                // 确保 _instanceMutex 可靠持有对象引用，绝不会在构造阶段因前持有者死亡抛 AbandonedMutexException。
                var m = new Mutex(false, mutexName);
                _instanceMutex = m;

                try
                {
                    isNew = m.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    // 前持有者崩溃或被强杀：当前线程已获得锁的所有权，安全接手，且 _instanceMutex 引用正常保留
                    isNew = true;
                }
            }
            catch (Exception ex)
            {
                // 极端环境（系统资源耗尽或安全策略拒绝）：记入调试输出并降级允许启动（fail-open）
                System.Diagnostics.Debug.WriteLine($"[Program] 互斥体创建失败 (fail-open): {ex.Message}");
                isNew = true;
            }

            if (!isNew)
            {
                // 唤醒当前数据目录所对应的既有运行实例
                NativeMethods.NotifyExistingInstance(ConfigService.ActivateMessageName);

                // 释放未获取到所有权的互斥体句柄
                try { _instanceMutex?.Dispose(); } catch { }
                _instanceMutex = null;

                // 显式退出码 2：便于父进程/脚本区分「已有实例运行跳过」与「失败」
                Environment.ExitCode = ExitCodeAlreadyRunning;
                return;
            }

            try
            {
                // 4. 仅新实例初始化 WPF 框架与主消息循环
                var app = new App();
                app.InitializeComponent();
                app.Run();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Program] 宿主未捕获顶级异常: {ex}");
                throw;
            }
            finally
            {
                try { _instanceMutex?.ReleaseMutex(); } catch { }
                try { _instanceMutex?.Dispose(); } catch { }
                _instanceMutex = null;
            }
        }

        /// <summary>
        /// 控制台转发：挂接父控制台 → 管道 RPC 调用 → 回显 → 返回退出码。
        /// </summary>
        private static int ForwardControlInvocation(string[] args)
        {
            ParentConsole.TryAttach();

            ControlArgs parsed;
            string parseError;
            if (!ControlArgs.TryParse(args, out parsed, out parseError))
            {
                Console.Error.WriteLine(parseError ?? "not a ctl invocation");
                Console.Error.WriteLine("usage: CarroDesk.exe ctl <capability> [--json] [--pin <pin>] [--pipe <name>] [--timeout <ms>] [--<param> <value> ...]");
                return 1;
            }

            var client = new PipeRpcClient(parsed.PipeName, parsed.TimeoutMs > 0 ? parsed.TimeoutMs : 3000);
            return ControlForwarder.Forward(parsed, client, Console.Out, Console.Error);
        }
    }
}
