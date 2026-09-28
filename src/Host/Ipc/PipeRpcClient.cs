using System;
using System.IO;
using System.IO.Pipes;
using CarroDesk.Core.Commands;
using CarroDesk.Host.Commands;

namespace CarroDesk.Host.Ipc
{
    /// <summary>
    /// 控制管道客户端（IPC 设计 §5.3，S3）：CLI / 主 exe 二实例转发 / MCP 瘦进程共用。
    /// 一次调用一条连接（请求-响应-关闭），与服务端的每连接模型对齐；
    /// 传输层失败一律折叠为 <see cref="CommandErrorCodes.TransportError"/>，绝不抛异常。
    /// </summary>
    public sealed class PipeRpcClient
    {
        private readonly string _pipeName;
        private readonly int _connectTimeoutMs;

        /// <param name="pipeName">空 = 默认 \\.\pipe\CarroDesk.ctl.&lt;当前用户SID&gt;（与服务端一致）。</param>
        public PipeRpcClient(string pipeName = null, int connectTimeoutMs = 3000)
        {
            _pipeName = string.IsNullOrWhiteSpace(pipeName)
                ? NamedPipeCommandServer.BuildDefaultPipeName()
                : pipeName.Trim();
            _connectTimeoutMs = connectTimeoutMs > 0 ? connectTimeoutMs : 3000;
        }

        public string PipeName { get { return _pipeName; } }

        public CommandResult Call(CommandRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            try
            {
                using (var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(_connectTimeoutMs);

                    var frame = FrameCodec.EncodeFrame(RpcProtocol.EncodeRequest(request));
                    pipe.Write(frame, 0, frame.Length);
                    pipe.Flush();

                    var response = ReadFrame(pipe);
                    if (response == null)
                        return CommandResult.Fail(CommandErrorCodes.TransportError, "connection closed without response");

                    object ignored;
                    return RpcProtocol.ParseResponse(response, out ignored);
                }
            }
            catch (Exception ex)
            {
                // TimeoutException（连接超时）/ UnauthorizedAccessException / IOException / ObjectDisposedException
                return CommandResult.Fail(CommandErrorCodes.TransportError, "pipe transport failed: " + ex.Message);
            }
        }

        private static byte[] ReadFrame(PipeStream stream)
        {
            var header = new byte[4];
            if (!ReadExactly(stream, header)) return null;
            var length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > FrameCodec.DefaultMaxFrameBytes)
                throw new IOException("response frame length out of range: " + length);
            var body = new byte[length];
            if (!ReadExactly(stream, body))
                throw new EndOfStreamException("stream ended mid-response");
            return body;
        }

        private static bool ReadExactly(PipeStream stream, byte[] buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }
    }
}
