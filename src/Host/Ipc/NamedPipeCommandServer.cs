using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using CarroDesk.Core;
using CarroDesk.Core.Commands;
using CarroDesk.Host.Commands;

namespace CarroDesk.Host.Ipc
{
    /// <summary>
    /// 命名管道控制服务（IPC 设计 §5.1，主通道传输适配器）。只做传输：
    /// 帧编解码 + JSON-RPC 映射，业务校验全部在 CommandHost。
    /// 安全：显式 PipeSecurity 仅授予当前用户 SID + SYSTEM 读写（拒绝其他账户）；
    /// 管道名带用户 SID，避免多用户/多会话撞名。超限帧直接断开。
    /// </summary>
    public sealed class NamedPipeCommandServer : IDisposable
    {
        private readonly CommandHost _host;
        private readonly ILoggerService _logger;
        private readonly int _maxFrameBytes;
        private readonly bool _useAcl;
        private CancellationTokenSource _cts;

        /// <param name="useAcl">false 仅限测试环境（同用户回环自测）；生产恒为 true。</param>
        public NamedPipeCommandServer(CommandHost host, string pipeName, ILoggerService logger,
            int maxFrameBytes = FrameCodec.DefaultMaxFrameBytes, bool useAcl = true)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            PipeName = pipeName;
            _logger = logger;
            _maxFrameBytes = maxFrameBytes;
            _useAcl = useAcl;
        }

        public string PipeName { get; }

        /// <summary>\\.\pipe\CarroDesk.ctl.&lt;当前用户SID&gt;；SID 取不到时退化为固定后缀。</summary>
        public static string BuildDefaultPipeName()
        {
            try
            {
                var sid = WindowsIdentity.GetCurrent() != null ? WindowsIdentity.GetCurrent().User : null;
                if (sid != null && !string.IsNullOrEmpty(sid.Value))
                    return @"\\.\pipe\CarroDesk.ctl." + sid.Value;
            }
            catch { /* 取 SID 失败不阻断启动 */ }
            return @"\\.\pipe\CarroDesk.ctl.default";
        }

        /// <summary>仅当前用户 + SYSTEM 读写。显式 DACL 取代默认管道 ACL。</summary>
        public static PipeSecurity BuildDefaultSecurity()
        {
            var security = new PipeSecurity();
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                if (identity != null && identity.User != null)
                {
                    security.AddAccessRule(new PipeAccessRule(
                        identity.User, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                }
            }
            catch { /* 规则构造失败时仍有 SYSTEM 规则兜底 */ }
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return security;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public void Dispose()
        {
            // net48 的管道异步等待不真正响应取消令牌；取消后循环会在下一次
            // 连接/失败周期退出，进程退出时后台任务随之终止，无需阻塞等待。
            _cts?.Cancel();
        }

        private NamedPipeServerStream CreateServerStream()
        {
            // 缓冲区必须显式给足：实测 0（系统默认）在「客户端流水线连发请求 + 服务端同时
            // 回写响应」时会双向写阻塞死锁（写操作要等对端读完才返回）。
            const int BufferSize = 64 * 1024;
            if (_useAcl)
            {
                // 现代 .NET 移除了带 PipeSecurity 的构造函数，改用 Acl 工厂方法
                return NamedPipeServerStreamAcl.Create(
                    PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    BufferSize, BufferSize, BuildDefaultSecurity());
            }
            return new NamedPipeServerStream(
                PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                BufferSize, BufferSize);
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = CreateServerStream();
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    CleanupStream(server);
                    return;
                }
                catch (Exception ex)
                {
                    CleanupStream(server);
                    _logger?.LogWarning("Ipc", "管道等待连接失败，0.5s 后重试: " + ex.Message);
                    try { await Task.Delay(500, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }

                // 每连接独立任务；accept 循环立即回到等待下一个实例（§5.1，照 GetItemsGuarded 隔离风格）
                var connection = server;
                var ignored = Task.Run(() => HandleConnectionAsync(connection, token));
            }
        }

        private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    byte[] frame;
                    try
                    {
                        frame = await FrameCodec.ReadFrameAsync(server, _maxFrameBytes).ConfigureAwait(false);
                    }
                    catch (InvalidDataException ex)
                    {
                        // 超限/坏帧：直接断开（IPC 设计 §5.1）
                        _logger?.LogWarning("Ipc", "帧非法，断开连接: " + ex.Message);
                        return;
                    }
                    catch (EndOfStreamException)
                    {
                        return;
                    }
                    if (frame == null) return; // 对端正常关闭

                    var call = RpcProtocol.ParseFrame(frame);
                    var result = call.IsParseError
                        ? CommandResult.Fail(CommandErrorCodes.ParseError, "parse error: request is not a valid JSON-RPC object")
                        : await _host.InvokeAsync(call.Request).ConfigureAwait(false);

                    var response = RpcProtocol.EncodeResult(call.Id, result);
                    var responseFrame = FrameCodec.EncodeFrame(response);
                    await server.WriteAsync(responseFrame, 0, responseFrame.Length).ConfigureAwait(false);
                    await server.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Ipc", "管道连接异常断开: " + ex.Message);
            }
            finally
            {
                CleanupStream(server);
            }
        }

        private static void CleanupStream(NamedPipeServerStream server)
        {
            if (server == null) return;
            try { if (server.IsConnected) server.Disconnect(); } catch { }
            try { server.Dispose(); } catch { }
        }
    }
}
