using System;
using System.IO;
using CarroDesk;
using CarroDesk.Common;
using CarroDesk.Host.Ipc;

namespace CarroDesk.Cli
{
    /// <summary>
    /// CarroDesk 控制台客户端（IPC 设计 §5.3/§5.5）。
    /// 用法：
    ///   CarroDesk.Cli ctl &lt;能力名&gt; [--json] [--pin &lt;口令&gt;] [--pipe &lt;名称&gt;] [--timeout &lt;ms&gt;] [--&lt;参数名&gt; &lt;值&gt;...]
    ///   CarroDesk.Cli --mcp    （S4：MCP stdio 瘦进程，stdout 只输出 JSON-RPC）
    /// 独立控制台工程形态（§10 决议 3 方案 A）：天生有控制台、stdout 干净、无 WPF 负担。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                // 固定 stdout 为 UTF-8：ctl 结果（含中文/JSON）常被重定向到管道或文件
                // 交给 jq / AI 助手解析，若沿用系统代码页（GBK 等）会乱码。
                // 重定向场景可能不支持，失败不致命。
                try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* ignore */ }

                if (args != null && args.Length > 0
                    && string.Equals(args[0], "--mcp", StringComparison.OrdinalIgnoreCase))
                {
                    // S4：MCP stdio 瘦进程——stdout 只输出协议 JSON，日志走 stderr
                    return McpStdioServer.Run(Console.In, Console.Out, Console.Error, () => new PipeRpcClient());
                }

                // 人肉 / AI 自助发现入口：--help / -h / help / ctl help / --version / -v
                string first = args != null && args.Length > 0 ? args[0] : null;
                string second = args != null && args.Length > 1 ? args[1] : null;
                bool ctlHelp = string.Equals(first, "ctl", StringComparison.OrdinalIgnoreCase) && IsHelp(second);
                if (IsHelp(first) || ctlHelp)
                {
                    PrintUsage(Console.Out);
                    return 0;
                }
                if (string.Equals(first, "--version", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(first, "-v", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Out.WriteLine("CarroDesk.Cli " + AppInfo.DisplayVersion);
                    Console.Out.WriteLine("informational: " + AppInfo.InformationalVersion);
                    Console.Out.WriteLine("runtime: " + AppInfo.DotNetRuntime);
                    return 0;
                }

                ControlArgs parsed;
                string parseError;
                if (!ControlArgs.TryParse(args, out parsed, out parseError))
                {
                    Console.Error.WriteLine(parseError ?? "not a ctl invocation");
                    PrintUsage(Console.Error);
                    return 1;
                }

                // --timeout 同时约束连接与等待响应：宿主卡死时 CLI 必须有界退出（H7）
                int timeoutMs = parsed.TimeoutMs > 0 ? parsed.TimeoutMs : 3000;
                var client = new PipeRpcClient(parsed.PipeName, timeoutMs, timeoutMs);
                return ControlForwarder.Forward(parsed, client, Console.Out, Console.Error);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("cli error: " + ex.Message);
                return 3;
            }
        }

        private static bool IsHelp(string token)
        {
            return string.Equals(token, "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-h", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "help", StringComparison.OrdinalIgnoreCase);
        }

        private static void PrintUsage(TextWriter w)
        {
            w.WriteLine("CarroDesk.Cli - Windows tray host control client");
            w.WriteLine("");
            w.WriteLine("usage:");
            w.WriteLine("  CarroDesk.Cli ctl <capability> [--json] [--pin <pin>] [--pipe <name>] [--timeout <ms>] [--<param> <value> ...]");
            w.WriteLine("  CarroDesk.Cli --mcp                 (MCP stdio adapter; stdout is protocol JSON only)");
            w.WriteLine("  CarroDesk.Cli --help | -h | help | ctl help");
            w.WriteLine("  CarroDesk.Cli --version | -v");
            w.WriteLine("");
            w.WriteLine("discover capabilities (host must be running):");
            w.WriteLine("  CarroDesk.Cli ctl host.capabilities.list --json");
            w.WriteLine("  CarroDesk.Cli ctl host.guide           (full AI-agent manual)");
            w.WriteLine("");
            w.WriteLine("examples:");
            w.WriteLine("  CarroDesk.Cli ctl host.status --json");
            w.WriteLine("  CarroDesk.Cli ctl awake.on --minutes 120");
            w.WriteLine("  CarroDesk.Cli ctl services.start --name GameViewerService");
            w.WriteLine("");
            w.WriteLine("exit codes: 0 ok | 1 usage | 2 capability/business error | 3 host unreachable");
            w.WriteLine("errors: -32001 module unavailable | -32002 PIN | -32003 rate limited | -32004 timeout | -32020 host not running");
            w.WriteLine("with --json, failures are written to stderr as {\"ok\":false,\"code\":<int>,\"message\":\"...\"}");
        }
    }
}
