using System;
using System.IO;
using CarroDesk.Core.Commands;
using CarroDesk.Host.Commands;
using Newtonsoft.Json;

namespace CarroDesk.Host.Ipc
{
    /// <summary>
    /// `ctl` 调用执行与结果回显（IPC 设计 §5.3，S3）。主 exe 二实例转发与 CarroDesk.Cli 共用；
    /// 输出统一走调用方注入的 TextWriter（主 exe 需先经 ParentConsole.TryAttach 挂接父控制台）。
    /// 退出码约定：0 = 执行成功；2 = 能力返回业务错误；3 = 传输失败；1 = 用法错误（调用方处理）。
    /// </summary>
    public static class ControlForwarder
    {
        public static int Forward(ControlArgs args, PipeRpcClient client, TextWriter output, TextWriter error)
        {
            if (args == null) throw new ArgumentNullException(nameof(args));
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (error == null) throw new ArgumentNullException(nameof(error));

            var request = new CommandRequest
            {
                Method = args.Method,
                Pin = args.Pin,
                Source = args.Source,
                Params = args.Params
            };
            var result = client.Call(request);

            if (result.Ok)
            {
                output.WriteLine(RpcProtocol.FormatPayload(result.Data, args.Json));
                return 0;
            }

            int code = result.Error != null ? result.Error.Code : CommandErrorCodes.Internal;
            string message = result.Error != null ? result.Error.Message : "unknown error";

            if (args.Json)
            {
                // --json 的错误路径也必须可程序化读取：固定 {"ok":false,"code":...,"message":...}。
                error.WriteLine("{\"ok\":false,\"code\":" + code +
                                ",\"message\":" + JsonConvert.ToString(message ?? "") + "}");
            }
            else
            {
                if (code == CommandErrorCodes.TransportError)
                {
                    message += "（宿主未运行？请先启动 CarroDesk 托盘程序）";
                }
                error.WriteLine("error " + code + ": " + message);
            }
            return code == CommandErrorCodes.TransportError ? 3 : 2;
        }
    }
}
