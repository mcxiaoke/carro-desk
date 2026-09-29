using System;
using System.Collections.Generic;
using System.Threading;

namespace CarroDesk.Services.Tasks
{
    /// <summary>
    /// 任务级单实例互斥（B2）：每个启用 singleInstance 的 detach 实例持有一个命名互斥体，
    /// 跨宿主重启仍然有效（同会话内）。参考 pm2/systemd 的"进程组唯一"语义。
    ///
    /// 命名空间用 Local\（会话内全局）：同登录会话中任何 CarroDesk 宿主实例（含新旧进程、
    /// 含便携/安装双副本）都能看到同一互斥体。跨用户会话不互斥——桌面任务本就按会话运行。
    ///
    /// 判定协议（三层，缺一不可）：
    ///   1) 进程内登记表先行——Mutex 有线程亲和，同线程重复 WaitOne 会重入成功而被误判为
    ///      "可获取"（已实测复现），同名互斥在本进程内的唯一性只能靠登记表保证；
    ///   2) createdNew=false 即互斥体已存在，默认视为"已有实例在运行"；
    ///   3) WaitOne 抛 AbandonedMutexException = 前持有者进程已死亡（崩溃/强杀），安全接手。
    ///
    /// 释放策略：Dispose 时不调用 ReleaseMutex（Mutex 有线程亲和，拥有线程已随 watcher
    /// 结束不可靠），直接弃置句柄即"放弃"（abandoned）。获取端把 abandoned 视为
    /// "前持有者已死亡、互斥体归我"——这正是我们的语义：互斥体被持有 = 实例在运行。
    /// 宿主进程整体崩溃时由内核关闭句柄，效果等同，天然自愈。
    /// </summary>
    internal sealed class TaskSingleInstanceMutex : IDisposable
    {
        private static readonly HashSet<string> HeldInProcess = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object HeldLock = new object();

        private Mutex _mutex;
        private readonly string _name;

        private TaskSingleInstanceMutex(Mutex mutex, string name)
        {
            _mutex = mutex;
            _name = name;
        }

        /// <summary>
        /// 尝试为任务获取单实例互斥体。
        /// 返回 true 且 mutex 可能非 null = 可以启动（acquired，或创建失败时 fail-open 不保护并记日志）；
        /// 返回 false = 已有实例在运行（互斥体被持有），不应启动。
        /// </summary>
        public static bool TryAcquire(string taskName, out TaskSingleInstanceMutex mutex)
        {
            mutex = null;
            if (string.IsNullOrWhiteSpace(taskName)) return true; // 无名任务不保护（校验已挡），fail-open
            string name = @"Local\CarroDesk.Task." + taskName;

            lock (HeldLock)
            {
                if (HeldInProcess.Contains(name)) return false;
            }

            try
            {
                // 任务名经 Validate 限定为 [a-zA-Z0-9_-]，可直接用作互斥体名
                bool createdNew;
                var m = new Mutex(false, name, out createdNew);
                bool acquired;
                try
                {
                    acquired = m.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                if (!acquired)
                {
                    m.Dispose();
                    return false;
                }
                lock (HeldLock) HeldInProcess.Add(name);
                mutex = new TaskSingleInstanceMutex(m, name);
                return true;
            }
            catch (Exception ex)
            {
                // 创建互斥体失败（系统资源等极少数情况）：fail-open，不阻止任务运行，只留日志
                try { TaskLogger.Warn(taskName, "singleInstance mutex create failed (fail-open): " + ex.Message); } catch { }
                return true;
            }
        }

        public void Dispose()
        {
            var m = _mutex;
            _mutex = null;
            if (m == null) return;
            lock (HeldLock) HeldInProcess.Remove(_name);
            try { m.Dispose(); } catch { }   // 弃置即放弃（abandoned），获取端按"前持有者死亡"处理
        }
    }
}
