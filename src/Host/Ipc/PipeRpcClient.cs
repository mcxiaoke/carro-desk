using System;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using CarroDesk.Core.Commands;

namespace CarroDesk.Host.Ipc
{
    /// <summary>
    /// 控制管道客户端（IPC 设计 §5.3，S3）：CLI / 主 exe 二实例转发 / MCP 瘦进程共用。
    /// 一次调用一条连接（请求-响应-关闭），与服务端的每连接模型对齐；
    /// 传输层失败一律折叠为 <see cref="CommandErrorCodes.TransportError"/>，绝不抛异常。
    /// </summary>
    public sealed class PipeRpcClient
    {
        private const int DefaultConnectTimeoutMs = 3000;
        private const int DefaultReadTimeoutMs = 10000;

        private readonly string _pipeName;
        private readonly int _connectTimeoutMs;
        private readonly int _readTimeoutMs;

        /// <param name="pipeName">空 = 默认 \\.\pipe\CarroDesk.ctl.&lt;当前用户SID&gt;（与服务端一致）。</param>
        /// <param name="connectTimeoutMs">连接超时；宿主未运行时快速失败。</param>
        /// <param name="readTimeoutMs">
        /// 等待响应的读超时（H7）。宿主卡死/只写半帧时必须有界返回，
        /// 否则 CLI、MCP 瘦进程与二实例转发会永久挂起。
        /// </param>
        public PipeRpcClient(string pipeName = null, int connectTimeoutMs = DefaultConnectTimeoutMs,
            int readTimeoutMs = DefaultReadTimeoutMs)
        {
            _pipeName = string.IsNullOrWhiteSpace(pipeName)
                ? NamedPipeCommandServer.BuildDefaultPipeName()
                : pipeName.Trim();
            _connectTimeoutMs = connectTimeoutMs > 0 ? connectTimeoutMs : DefaultConnectTimeoutMs;
            _readTimeoutMs = readTimeoutMs > 0 ? readTimeoutMs : DefaultReadTimeoutMs;
        }

        public string PipeName { get { return _pipeName; } }

        public CommandResult Call(CommandRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            try
            {
                // 必须用异步句柄：同步句柄上的挂起 Read 无法被取消，
                // 读超时后 CloseHandle 会一直等 I/O 完成，等于没超时（H7）。
                using (var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(_connectTimeoutMs);

                    var frame = FrameCodec.EncodeFrame(RpcProtocol.EncodeRequest(request));
                    pipe.Write(frame, 0, frame.Length);
                    pipe.Flush();

                    var response = ReadFrameWithTimeout(pipe);
                    if (response == null)
                        return CommandResult.Fail(CommandErrorCodes.TransportError, "connection closed without response");

                    object ignored;
                    return RpcProtocol.ParseResponse(response, out ignored);
                }
            }
            catch (Exception ex)
            {
                // TimeoutException（连接/读超时）/ UnauthorizedAccessException / IOException / ObjectDisposedException
                return CommandResult.Fail(CommandErrorCodes.TransportError, "pipe transport failed: " + ex.Message);
            }
        }

        /// <summary>带读超时的整帧读取；超时后关闭流，避免挂起的异步读长期占用连接。</summary>
        private byte[] ReadFrameWithTimeout(PipeStream pipe)
        {
            var readTask = ReadFrameAsync(pipe);
            try
            {
                return readTask.WaitAsync(TimeSpan.FromMilliseconds(_readTimeoutMs)).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                try { pipe.Dispose(); } catch { }
                // 观察被遗弃任务的异常，避免 UnobservedTaskException 噪音
                readTask.ContinueWith(t => { var ignored = t.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                throw new TimeoutException("timeout after " + _readTimeoutMs + "ms waiting for response");
            }
        }

        private static async Task<byte[]> ReadFrameAsync(PipeStream stream)
        {
            var header = new byte[4];
            if (!await ReadExactlyAsync(stream, header).ConfigureAwait(false)) return null;
            var length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > FrameCodec.DefaultMaxFrameBytes)
                throw new IOException("response frame length out of range: " + length);
            var body = new byte[length];
            if (!await ReadExactlyAsync(stream, body).ConfigureAwait(false))
                throw new EndOfStreamException("stream ended mid-response");
            return body;
        }

        private static async Task<bool> ReadExactlyAsync(PipeStream stream, byte[] buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer, read, buffer.Length - read).ConfigureAwait(false);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }
    }
}
