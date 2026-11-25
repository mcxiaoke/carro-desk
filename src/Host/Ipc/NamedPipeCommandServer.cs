using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using CarroDesk.Common;
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
        private readonly PipeSecurity _cachedSecurity;
        private readonly object _lifecycleLock = new object();
        private readonly HashSet<NamedPipeServerStream> _activeStreams = new HashSet<NamedPipeServerStream>();
        private CancellationTokenSource _cts;
        private NamedPipeServerStream _pendingServer;
        private bool _started;

        /// <param name="useAcl">false 仅限测试环境（同用户回环自测）；生产恒为 true。</param>
        public NamedPipeCommandServer(CommandHost host, string pipeName, ILoggerService logger,
            int maxFrameBytes = FrameCodec.DefaultMaxFrameBytes, bool useAcl = true)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            PipeName = pipeName;
            _logger = logger;
            _maxFrameBytes = maxFrameBytes;
            _useAcl = useAcl;
            _cachedSecurity = useAcl ? BuildDefaultSecurity() : null;
        }

        public string PipeName { get; }

        /// <summary>\\.\pipe\CarroDesk.ctl.&lt;当前用户SID&gt;；SID 取不到时退化为固定后缀。</summary>
        public static string BuildDefaultPipeName()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var sid = identity != null ? identity.User : null;
                    if (sid != null && !string.IsNullOrEmpty(sid.Value))
                        return @"\\.\pipe\CarroDesk.ctl." + sid.Value;
                }
            }
            catch (Exception ex)
            {
                // 退化到固定管道名；必须留痕，否则排障时无从判断为何连的是 .default
                StaticLog.Warn("Ipc", "读取当前用户 SID 失败，管道名退化为 .default: " + ex.Message);
            }
            return @"\\.\pipe\CarroDesk.ctl.default";
        }

        /// <summary>仅当前用户 + SYSTEM 读写。显式 DACL 取代默认管道 ACL。</summary>
        public static PipeSecurity BuildDefaultSecurity()
        {
            var security = new PipeSecurity();
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    if (identity != null && identity.User != null)
                    {
                        security.AddAccessRule(new PipeAccessRule(
                            identity.User, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    }
                }
            }
            catch (Exception ex)
            {
                // 只剩 SYSTEM 规则时当前用户连不上管道，IPC 会静默不可用
                StaticLog.Warn("Ipc", "当前用户 SID 读取失败，管道 ACL 仅剩 SYSTEM，IPC 可能不可用: " + ex.Message);
            }
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return security;
        }

        public void Start()
        {
            CancellationTokenSource cts;
            lock (_lifecycleLock)
            {
                if (_started) return;
                _started = true;
                cts = new CancellationTokenSource();
                _cts = cts;
            }

            // 由 accept 循环结束后的 continuation 统一释放 cts，避免 Dispose 期间后台任务遇到 ObjectDisposedException。
            _ = Task.Run(() => AcceptLoopAsync(cts.Token)).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    StaticLog.Warn("Ipc", "管道 accept 循环异常终止: " + t.Exception.GetBaseException().Message);
                }
                try { cts.Dispose(); } catch { }
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        public void Dispose()
        {
            CancellationTokenSource cts;
            NamedPipeServerStream pending;
            NamedPipeServerStream[] active;
            lock (_lifecycleLock)
            {
                if (!_started && _cts == null && _pendingServer == null && _activeStreams.Count == 0)
                    return;
                _started = false;
                cts = _cts;
                _cts = null;
                pending = _pendingServer;
                _pendingServer = null;
                active = _activeStreams.Count > 0 ? new List<NamedPipeServerStream>(_activeStreams).ToArray() : new NamedPipeServerStream[0];
                _activeStreams.Clear();
            }

            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }
            CleanupStream(pending);
            foreach (var stream in active)
            {
                CleanupStream(stream);
            }
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
                    BufferSize, BufferSize, _cachedSecurity ?? BuildDefaultSecurity());
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
                    lock (_lifecycleLock)
                    {
                        if (token.IsCancellationRequested || !_started)
                        {
                            CleanupStream(server);
                            return;
                        }
                        _pendingServer = server;
                    }
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ClearPendingServer(server);
                    CleanupStream(server);
                    return;
                }
                catch (ObjectDisposedException)
                {
                    ClearPendingServer(server);
                    if (token.IsCancellationRequested) return;
                    continue;
                }
                catch (Exception ex)
                {
                    ClearPendingServer(server);
                    CleanupStream(server);
                    _logger?.LogWarning("Ipc", "管道等待连接失败，0.5s 后重试: " + ex.Message);
                    try { await Task.Delay(500, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }

                bool accepted = false;
                lock (_lifecycleLock)
                {
                    if (_pendingServer == server) _pendingServer = null;
                    if (_started && !token.IsCancellationRequested)
                    {
                        _activeStreams.Add(server);
                        accepted = true;
                    }
                }
                if (!accepted)
                {
                    CleanupStream(server);
                    return;
                }

                // 每连接独立任务；accept 循环立即回到等待下一个实例（§5.1，照 GetItemsGuarded 隔离风格）
                var connection = server;
                var ignored = Task.Run(() => HandleConnectionAsync(connection, token));
            }
        }

        private void ClearPendingServer(NamedPipeServerStream server)
        {
            if (server == null) return;
            lock (_lifecycleLock)
            {
                if (_pendingServer == server) _pendingServer = null;
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
                        frame = await FrameCodec.ReadFrameAsync(server, _maxFrameBytes, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
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
                    await server.WriteAsync(responseFrame, 0, responseFrame.Length, token).ConfigureAwait(false);
                    await server.FlushAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 服务端正在停止
            }
            catch (ObjectDisposedException)
            {
                // 服务端 Dispose 时强制关流
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    _logger?.LogWarning("Ipc", "管道连接异常断开: " + ex.Message);
            }
            finally
            {
                lock (_lifecycleLock)
                {
                    _activeStreams.Remove(server);
                }
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
