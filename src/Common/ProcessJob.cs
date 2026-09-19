using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CarroDesk.Common
{
    /// <summary>
    /// 进程作业（Job Object）封装。
    ///
    /// 用途：把任务启动的子进程挂到一个作业对象上，从而能够一次性回收"整棵子进程树"。
    /// 解决的问题：
    ///   1) 任务超时后 KillTree 依赖 taskkill /T，失败时残留孙进程；
    ///   2) 宿主退出时正在运行的任务进程树无人回收，成为孤儿进程。
    ///
    /// 设计约束：全部原生调用失败都必须降级为"不影响任务自身运行"，
    /// 因此 <see cref="TryCreate"/> / <see cref="TryAssign"/> 都返回结果而不抛异常。
    /// 注意：启用 kill-on-close 后，宿主进程退出会连带结束其仍在运行的任务进程树，
    /// 这是"不留孤儿进程"的必然代价。
    /// </summary>
    public sealed class ProcessJob : IDisposable
    {
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        private const int JobObjectExtendedLimitInformation = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private IntPtr _handle = IntPtr.Zero;

        private ProcessJob(IntPtr handle)
        {
            _handle = handle;
        }

        public bool IsValid
        {
            get { return _handle != IntPtr.Zero; }
        }

        /// <summary>创建作业对象；任何失败都返回 null（调用方退化为原行为）。</summary>
        public static ProcessJob TryCreate(bool killOnJobClose = true)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero) return null;

                if (killOnJobClose)
                {
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

                    int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                    IntPtr buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.StructureToPtr(info, buffer, false);
                        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
                        {
                            // 设置失败不致命：作业仍可创建，只是关闭句柄时不会连带杀进程树
                            Debug.WriteLine("[ProcessJob] SetInformationJobObject failed, err=" + Marshal.GetLastWin32Error());
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }

                return new ProcessJob(handle);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ProcessJob] TryCreate failed: " + ex.Message);
                if (handle != IntPtr.Zero)
                {
                    try { CloseHandle(handle); } catch { }
                }
                return null;
            }
        }

        /// <summary>
        /// 把已启动的进程加入作业。失败返回 false（例如宿主自身已处于不允许嵌套的作业中），
        /// 调用方应继续按原逻辑执行任务。
        /// </summary>
        public bool TryAssign(Process process)
        {
            if (_handle == IntPtr.Zero || process == null) return false;
            try
            {
                if (process.HasExited) return false;
                return AssignProcessToJobObject(_handle, process.Handle);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ProcessJob] TryAssign failed: " + ex.Message);
                return false;
            }
        }

        public void Dispose()
        {
            var handle = _handle;
            _handle = IntPtr.Zero;
            if (handle == IntPtr.Zero) return;

            try { CloseHandle(handle); }
            catch (Exception ex) { Debug.WriteLine("[ProcessJob] CloseHandle failed: " + ex.Message); }
        }
    }
}
