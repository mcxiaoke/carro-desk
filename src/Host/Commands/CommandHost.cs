using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CarroDesk.Core;
using CarroDesk.Core.Commands;
using CarroDesk.Services;

namespace CarroDesk.Host.Commands
{
    /// <summary>
    /// 命令分发器（IPC 设计 §4.3）。校验顺序不可调换：
    /// 存在性(-32601) → 模块 Running(-32001) → 参数(-32602) → 口令(-32002) → 限流(-32003)
    /// → 执行（超时 -32004 / 内部 -32010）。
    /// 对任何输入都不抛异常；成功与失败都审计；内核不认识传输，输入只有能力名 + 参数字典。
    /// </summary>
    public sealed class CommandHost
    {
        private const int DefaultTimeoutMs = 5000;

        private readonly CommandRegistry _registry;
        private readonly Func<string, ModuleStatus> _moduleStatus;
        private readonly PinGuard _pinGuard;
        private readonly ICommandAuditSink _audit;
        private readonly CommandRateLimiter _rateLimiter;
        private readonly ILoggerService _logger;

        /// <param name="moduleStatus">模块状态查询；宿主自身能力（moduleId="host"）由调用方保证返回 Running。</param>
        /// <param name="pinGuard">口令挑战（含失败限流）。为 null 时 RequiresPin 能力一律拒绝（fail closed）。</param>
        public CommandHost(
            CommandRegistry registry,
            Func<string, ModuleStatus> moduleStatus,
            PinGuard pinGuard,
            ICommandAuditSink audit,
            CommandRateLimiter rateLimiter = null,
            ILoggerService logger = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _moduleStatus = moduleStatus ?? throw new ArgumentNullException(nameof(moduleStatus));
            _pinGuard = pinGuard;
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
            _rateLimiter = rateLimiter ?? new CommandRateLimiter();
            _logger = logger;
        }

        /// <summary>
        /// 同步执行入口，仅供不需要 await 的调用方（如单测）使用。
        /// 传输适配层一律用 <see cref="InvokeAsync"/>；勿在 UI 线程调用本方法
        /// （会阻塞 UI 线程直到能力超时）。
        /// </summary>
        public CommandResult Invoke(CommandRequest request)
        {
            return InvokeAsync(request).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 异步执行：校验链是毫秒级同步工作，入口立即让出；能力执行阶段等待期间
        /// 不占用任何线程（见 <see cref="InvokeHandler"/>）。异常绝不逃逸，审计照常。
        /// </summary>
        public async Task<CommandResult> InvokeAsync(CommandRequest request)
        {
            var sw = Stopwatch.StartNew();
            CommandResult result;
            try
            {
                result = await InvokeCoreAsync(request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 内核绝对不逃逸异常：任何未预期错误折叠为 -32010
                result = CommandResult.Fail(CommandErrorCodes.Internal, "internal error: " + ex.Message);
                _logger?.LogError("Commands", "命令分发发生未预期异常", ex);
            }

            _audit.Write(new CommandAuditEntry
            {
                Ts = DateTime.Now,
                Source = request != null ? request.Source : null,
                Method = request != null ? request.Method : null,
                ParamsDigest = FileCommandAuditSink.DigestParams(request != null ? request.Params : null),
                Ok = result.Ok,
                Code = result.Ok ? 0 : (result.Error != null ? result.Error.Code : CommandErrorCodes.Internal),
                ElapsedMs = sw.ElapsedMilliseconds,
                PinUsed = request != null && !string.IsNullOrEmpty(request.Pin)
            });
            return result;
        }

        private async Task<CommandResult> InvokeCoreAsync(CommandRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Method))
                return Fail(CommandErrorCodes.CapabilityNotFound, "capability not found");

            CommandDescriptor descriptor;
            if (!_registry.TryGet(request.Method, out descriptor))
                return Fail(CommandErrorCodes.CapabilityNotFound, "capability not found: " + request.Method);

            // 2 → 3：能力在白名单上，还要求所属模块处于 Running
            var status = _moduleStatus(descriptor.ModuleId);
            if (status != ModuleStatus.Running)
                return Fail(CommandErrorCodes.ModuleUnavailable,
                    "module '" + descriptor.ModuleId + "' is not running (status: " + status + ")");

            CommandRequest effective;
            var validation = ValidateParams(descriptor, request, out effective);
            if (!validation.Ok) return validation;

            if (descriptor.RequiresPinFor != null ? descriptor.RequiresPinFor(effective) : descriptor.RequiresPin)
            {
                if (_pinGuard == null)
                    return Fail(CommandErrorCodes.PinRequired, "pin required");
                TimeSpan blockRemaining;
                var attempt = _pinGuard.Try(effective.Pin, out blockRemaining);
                if (attempt == PinAttemptResult.Blocked)
                    return Fail(CommandErrorCodes.PinRequired,
                        "pin blocked for " + (int)Math.Ceiling(blockRemaining.TotalSeconds) + "s");
                if (attempt != PinAttemptResult.Success)
                    return Fail(CommandErrorCodes.PinRequired, "pin rejected");
            }

            if (!_rateLimiter.TryAcquire(descriptor.Name, effective.Source))
                return Fail(CommandErrorCodes.RateLimited, "rate limited: " + descriptor.Name);

            return await InvokeHandler(descriptor, effective).ConfigureAwait(false);
        }

        /// <summary>
        /// 执行阶段（第7 步）。
        ///
        /// 为什么不用 <c>Task.Wait(timeout)</c>：那会让调用线程阻塞在等待上，
        /// 而 handler 本身已在线程池上跑 —— 每条并发命令白占两个线程池线程（一个纯等待），
        /// 命名管道允许多连接，这个浪费会被直接放大。改用
        /// <see cref="Task.WhenAny(Task, Task)"/>：等待期间当前线程完全释放。
        ///
        /// 超时语义：返回 -32004，并 <see cref="CancellationTokenSource.Cancel()"/>
        /// 通知 CancellableHandler 尽快退出。取消只是"尽力而为"的协作式通知，
        /// 不强制中断 —— 已提交给外部系统的操作无法回滚。
        /// </summary>
        private async Task<CommandResult> InvokeHandler(CommandDescriptor descriptor, CommandRequest effective)
        {
            var handler = descriptor.Handler;
            var cancellable = descriptor.CancellableHandler;
            if (handler == null && cancellable == null)
                return Fail(CommandErrorCodes.Internal, "descriptor has no handler");

            var timeoutMs = descriptor.TimeoutMs > 0 ? descriptor.TimeoutMs : DefaultTimeoutMs;
            var captured = effective;
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                Task<CommandResult> task = Task.Run(() =>
                {
                    if (cancellable != null) return cancellable(captured, cts.Token);
                    return handler(captured);
                });

                var finished = await Task.WhenAny(task, DelayUntilCancelled(cts)).ConfigureAwait(false);
                if (finished != task)
                {
                    // 超时：取消并观察 eventual 异常，避免未观察异常在 GC 时引发
                    // TaskScheduler.UnobservedTaskException（.NET 4.5 起不再终止进程，
                    // 但会静默丢失 handler 的真实失败原因）。
                    CancelQuietly(cts);
                    ObserveLateCompletion(task, descriptor, timeoutMs);
                    return Fail(CommandErrorCodes.Timeout, "timeout after " + timeoutMs + "ms");
                }

                CommandResult handlerResult;
                try
                {
                    handlerResult = await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // handler 主动响应取消而退出：与超时同义，调用方看到的是超时而非内部错误
                    return Fail(CommandErrorCodes.Timeout, "timeout after " + timeoutMs + "ms (handler cancelled)");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(descriptor.ModuleId, "能力 '" + descriptor.Name + "' 执行异常", ex);
                    return Fail(CommandErrorCodes.Internal, "internal error: " + ex.Message);
                }
                finally
                {
                    // handler 已结束，取消只是为了释放仍在挂着的超时定时器
                    // （Task.Delay(Infinite, token) 不会因 CTS 被 Dispose 而撤销）
                    CancelQuietly(cts);
                }

                if (handlerResult == null)
                    return Fail(CommandErrorCodes.Internal, "handler returned no result");
                return handlerResult;
            }
        }

        private static void CancelQuietly(CancellationTokenSource cts)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { /* 回调自身抛异常，不影响返回结果 */ }
        }

        /// <summary>超时信号：与<paramref name="cts"/> 同时到期的延迟任务。</summary>
        private static async Task DelayUntilCancelled(CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 超时信号本身不需要结果，WhenAny 只看"已完成"
            }
        }

        /// <summary>
        /// 超时后仍在运行的 handler：必须观察其异常，否则异常会随Task 被终结而
        /// 变成 UnobservedTaskException；同时记一条日志 —— 此刻调用方已经拿到
        /// -32004，若handler 的副作用稍后才落地，这是唯一的排障线索。
        /// </summary>
        private void ObserveLateCompletion(Task<CommandResult> task, CommandDescriptor descriptor, int timeoutMs)
        {
            task.ContinueWith(t =>
            {
                var ignored = t.Exception;
                try
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        _logger?.LogError(descriptor.ModuleId, "能力 '" + descriptor.Name + "' 超时后异常",
                            t.Exception.GetBaseException());
                    }
                    else
                    {
                        _logger?.LogWarning(descriptor.ModuleId,
                            "能力 '" + descriptor.Name + "' 在超时(" + timeoutMs + "ms)后仍在后台完成，副作用可能已生效");
                    }
                }
                catch { }
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        /// <summary>
        /// 参数校验 + 规整（IPC 设计 §4.3 第 4 步）：未知参数名拒绝、类型收敛为
        /// string/int/bool 原始类型、AllowedValues 精确匹配；通过后 handler 收到的是规整后的副本。
        /// </summary>
        private static CommandResult ValidateParams(CommandDescriptor descriptor, CommandRequest request, out CommandRequest effective)
        {
            effective = request;
            var specs = descriptor.Params ?? new CommandParam[0];
            var raw = request.Params ?? new Dictionary<string, object>();

            foreach (var key in raw.Keys)
            {
                var known = specs.Any(p => p != null && string.Equals(p.Name, key, StringComparison.Ordinal));
                if (!known)
                    return Fail(CommandErrorCodes.InvalidParams, "unknown param: " + key);
            }

            var normalized = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var spec in specs)
            {
                if (spec == null || string.IsNullOrEmpty(spec.Name)) continue;

                object value;
                var present = raw.TryGetValue(spec.Name, out value);
                if (!present || value == null || (value is string && ((string)value).Length == 0))
                {
                    if (spec.Required)
                        return Fail(CommandErrorCodes.InvalidParams, "missing required param: " + spec.Name);
                    continue;
                }

                object converted;
                string error;
                if (!TryConvert(spec, value, out converted, out error))
                    return Fail(CommandErrorCodes.InvalidParams, "param '" + spec.Name + "': " + error);

                if (spec.AllowedValues != null && spec.AllowedValues.Length > 0)
                {
                    var asString = Convert.ToString(converted, CultureInfo.InvariantCulture);
                    if (Array.IndexOf(spec.AllowedValues, asString) < 0)
                        return Fail(CommandErrorCodes.InvalidParams,
                            "param '" + spec.Name + "' must be one of: " + string.Join(", ", spec.AllowedValues));
                }

                normalized[spec.Name] = converted;
            }

            effective = new CommandRequest
            {
                Method = request.Method,
                Pin = request.Pin,
                Source = request.Source,
                Params = normalized
            };
            return CommandResult.Success();
        }

        private static bool TryConvert(CommandParam spec, object value, out object converted, out string error)
        {
            converted = null;
            error = null;
            var type = (spec.Type ?? "string").Trim().ToLowerInvariant();

            if (type == "int")
            {
                if (value is sbyte || value is byte || value is short || value is ushort
                    || value is int || value is uint)
                {
                    converted = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return true;
                }
                if (value is long || value is ulong)
                {
                    var l = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    if (l < int.MinValue || l > int.MaxValue)
                    {
                        error = "expected int, got out-of-range number";
                        return false;
                    }
                    converted = (int)l;
                    return true;
                }
                if (value is double || value is float || value is decimal)
                {
                    // JSON 数字若无小数部分也可能被解析为 double（如 5.0）；此处按整数语义收敛，
                    // 非整数则明确报错，绝不能静默降级为字符串（否则 int 参数会拿到 "5"）。
                    var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue)
                    {
                        converted = (int)d;
                        return true;
                    }
                    error = "expected int, got non-integral number";
                    return false;
                }
                var s = value as string;
                int parsed;
                if (s != null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    converted = parsed;
                    return true;
                }
                error = "expected int";
                return false;
            }

            if (type == "bool")
            {
                if (value is bool)
                {
                    converted = value;
                    return true;
                }
                var s = value as string;
                if (s != null)
                {
                    if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1")
                    {
                        converted = true;
                        return true;
                    }
                    if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) || s == "0")
                    {
                        converted = false;
                        return true;
                    }
                }
                error = "expected bool";
                return false;
            }

            // string 及未声明类型的兜底
            if (value is string)
            {
                converted = value;
                return true;
            }
            converted = Convert.ToString(value, CultureInfo.InvariantCulture);
            return true;
        }

        private static CommandResult Fail(int code, string message)
        {
            return CommandResult.Fail(code, message);
        }
    }
}
