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
        /// <para><b>仅限非 UI 线程使用</b>：本实现用 <c>task.Wait(timeout)</c> 同步等待，
        /// 在 UI 线程上调用会冻结界面（每个守卫最长 3 秒）。UI 线程场景请用
        /// <see cref="RunTimeoutAsync"/>。另外超时后 <c>action</c> 仍会在线程池上继续跑完，
        /// 无法中途终止——这是 .NET 不提供协作取消时的固有代价。</para>
        /// </summary>
        public static bool RunTimeout(string moduleId, TimeSpan timeout, Action action, Action<string, Exception> onError = null)
        {
            bool success = false;
            using (var cts = new System.Threading.CancellationTokenSource(timeout))
            {
                try
                {
                    var task = System.Threading.Tasks.Task.Run(action, cts.Token);
                    success = task.Wait(timeout) && task.IsCompleted && !task.IsFaulted && !task.IsCanceled;
                }
                catch (Exception ex)
                {
                    onError?.Invoke(moduleId, ex);
                    success = false;
                }
            }
            return success;
        }

        /// <summary>
        /// <see cref="RunTimeout"/> 的异步版本：**不阻塞调用线程**，供 UI 线程上的
        /// 退出守卫协商使用。超时同样视为放行（返回 false）。
        /// 完成后回到调用方的同步上下文（WPF 下即 UI 线程），可安全继续操作界面。
        /// </summary>
        public static async System.Threading.Tasks.Task<bool> RunTimeoutAsync(
            string moduleId, TimeSpan timeout, Action action, Action<string, Exception> onError = null)
        {
            if (action == null) return true;

            try
            {
                var task = System.Threading.Tasks.Task.Run(action);
                var completed = await System.Threading.Tasks.Task.WhenAny(task, System.Threading.Tasks.Task.Delay(timeout)).ConfigureAwait(true);

                if (completed != task)
                {
                    onError?.Invoke(moduleId, new TimeoutException("exit guard timeout: " + timeout));
                    return false;
                }

                // 等真正完成的任务，让异常进入下面的 catch 而不是被 UnobservedTaskException 兜底
                await task.ConfigureAwait(true);
                return true;
            }
            catch (Exception ex)
            {
                onError?.Invoke(moduleId, ex);
                return false;
            }
        }
    }
}