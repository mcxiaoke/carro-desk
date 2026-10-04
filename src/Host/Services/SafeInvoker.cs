using System;

namespace CarroDesk.Host.Services
{
    /// <summary>
    /// 统一的模块回调安全包裹（规范 §6.2）。
    /// 捕获一切异常并转交错误处理器，保证不击穿宿主。
    /// </summary>
    public static class SafeInvoker
    {
        public static bool Run(string moduleId, Action action, Action<string, Exception> onError = null)
        {
            if (action == null) return true;
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                onError?.Invoke(moduleId, ex);
                return false;
            }
        }

        /// <summary>
        /// 带超时包裹（规范 §3.5 退出守卫用，超时视为放行 false）。
        ///
        /// <para><b>仅限非 UI 线程使用</b>：本实现同步等待，在 UI 线程上调用会冻结界面
        /// （每个守卫最长 3 秒）。UI 线程场景请用 <see cref="RunTimeoutAsync"/>。
        /// 另请注意超时只是"停止等待"：<c>action</c> 仍会在线程池上跑完，无法中途终止。
        /// 需要感知取消的 action 请改用带<see cref="CancellationToken"/> 的
        /// <c>RunTimeoutAsync</c> 重载。</para>
        /// </summary>
        public static bool RunTimeout(string moduleId, TimeSpan timeout, Action action, Action<string, Exception> onError = null)
        {
            if (action == null) return true;
            bool success = false;
            System.Threading.Tasks.Task task = null;
            try
            {
                task = System.Threading.Tasks.Task.Run(action);
                success = task.Wait(timeout) && task.IsCompleted && !task.IsFaulted && !task.IsCanceled;
            }
            catch (Exception ex)
            {
                onError?.Invoke(moduleId, ex);
                success = false;
            }
            // 与"超时后仍在后台跑"的语义对齐：观察 eventual 异常，
            // 否则会变成 UnobservedTaskException，丢失真实失败原因。
            if (!success && task != null && !task.IsCompleted) ObserveLateFailure(task);
            return success;
        }

        private static void ObserveLateFailure(System.Threading.Tasks.Task task)
        {
            task.ContinueWith(t =>
            {
                var ignored = t.Exception;
            }, System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
        }

        /// <summary>
        /// <see cref="RunTimeout"/> 的异步版本：**不阻塞调用线程**，供 UI 线程上的
        /// 退出守卫协商使用。超时同样视为放行（返回 false）。
        /// 完成后回到调用方的同步上下文（WPF 下即 UI 线程），可安全继续操作界面。
        /// </summary>
        public static System.Threading.Tasks.Task<bool> RunTimeoutAsync(
            string moduleId, TimeSpan timeout, Action action, Action<string, Exception> onError = null)
        {
            if (action == null) return System.Threading.Tasks.Task.FromResult(true);
            return RunTimeoutAsync(moduleId, timeout, _ => action(), onError);
        }

        /// <summary>
        /// 可取消版本：超时时先发出取消信号再返回 false。
        /// 与 <see cref="RunTimeoutAsync(string,TimeSpan,Action,Action{string,Exception})"/>
        /// 的区别是 action 能看到令牌，从而真正停止后续步骤 —— 否则超时只是"停止等待"，
        /// 回调仍会跑完，调用方看到失败时副作用却已发生。
        /// </summary>
        public static async System.Threading.Tasks.Task<bool> RunTimeoutAsync(
            string moduleId,
            TimeSpan timeout,
            Action<System.Threading.CancellationToken> action,
            Action<string, Exception> onError = null)
        {
            if (action == null) return true;

            // cts 必须活到方法结束：不能由调用方 using 后立刻释放，
            // 也不能在这里把 Task 直接扔出去（那样 using 会在 await 完成前就Dispose）。
            var cts = new System.Threading.CancellationTokenSource(timeout);
            System.Threading.Tasks.Task task = null;
            try
            {
                task = System.Threading.Tasks.Task.Run(() => action(cts.Token));
                var completed = await System.Threading.Tasks.Task
                    .WhenAny(task, System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite, cts.Token))
                    .ConfigureAwait(true);

                if (completed != task)
                {
                    // 超时：发出取消信号，让仍能观察令牌的 action 尽快收手
                    try { cts.Cancel(); } catch (ObjectDisposedException) { }
                    onError?.Invoke(moduleId, new TimeoutException("exit guard timeout: " + timeout));
                    ObserveLateFailure(task);
                    return false;
                }

                // 等真正完成的任务，让异常进入下面的 catch 而不是被 UnobservedTaskException 兜底
                await task.ConfigureAwait(true);
                return true;
            }
            catch (Exception ex)
            {
                onError?.Invoke(moduleId, ex);
                if (task != null && !task.IsCompleted) ObserveLateFailure(task);
                return false;
            }
            finally
            {
                cts.Dispose();
            }
        }
    }
}