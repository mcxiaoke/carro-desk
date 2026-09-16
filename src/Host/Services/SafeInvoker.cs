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
    }
}