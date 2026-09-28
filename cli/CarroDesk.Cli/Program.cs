using System;
using CarroDesk;
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
                if (args != null && args.Length > 0
                    && string.Equals(args[0], "--mcp", StringComparison.OrdinalIgnoreCase))
                {
                    // S4：MCP stdio 瘦进程——stdout 只输出协议 JSON，日志走 stderr
                    try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 重定向场景可能不支持 */ }
                    return McpStdioServer.Run(Console.In, Console.Out, Console.Error, () => new PipeRpcClient());
                }

                ControlArgs parsed;
                string parseError;
                if (!ControlArgs.TryParse(args, out parsed, out parseError))
                {
                    Console.Error.WriteLine(parseError ?? "not a ctl invocation");
                    PrintUsage();
                    return 1;
                }

                var client = new PipeRpcClient(parsed.PipeName, parsed.TimeoutMs > 0 ? parsed.TimeoutMs : 3000);
                return ControlForwarder.Forward(parsed, client, Console.Out, Console.Error);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("cli error: " + ex.Message);
                return 3;
            }
        }

        private static void PrintUsage()
        {
            Console.Error.WriteLine("usage:");
            Console.Error.WriteLine("  CarroDesk.Cli ctl <capability> [--json] [--pin <pin>] [--pipe <name>] [--timeout <ms>] [--<param> <value> ...]");
            Console.Error.WriteLine("  CarroDesk.Cli --mcp");
            Console.Error.WriteLine("examples:");
            Console.Error.WriteLine("  CarroDesk.Cli ctl host.status --json");
            Console.Error.WriteLine("  CarroDesk.Cli ctl host.capabilities.list");
        }
    }
}
