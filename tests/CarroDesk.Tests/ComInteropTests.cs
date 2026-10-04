using System;
using System.Reflection;
using System.Runtime.InteropServices;
using CarroDesk.Common;
using CarroDesk.Core.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// COM 互操作封送布局测试（P0-4）。
    /// </summary>
    [TestClass]
    public class ComInteropTests
    {
        [TestMethod]
        public void PropVariant_MarshalledSize_IsAtLeastNativeSize()
        {
            // 原生 PROPVARIANT：x86 = 16 字节，x64 = 24 字节。
            // 声明尺寸偏小会让 IPropertyStore.GetValue 越界写原生内存；
            // 偏大是安全的（原生写得比我们分配的少）。
            int expectedMinimum = IntPtr.Size == 8 ? 24 : 16;
            int actual = Marshal.SizeOf(typeof(PropVariant));

            Assert.IsTrue(actual >= expectedMinimum,
                string.Format("PropVariant 封送尺寸 {0} 小于原生要求的 {1}，会导致越界写", actual, expectedMinimum));
        }

        [TestMethod]
        public void PropVariant_UnionFields_ShareOffsetEight()
        {
            // union 各成员必须共用同一偏移，否则取值会读到错误的字节
            Assert.AreEqual(8, (int)Marshal.OffsetOf(typeof(PropVariant), "pwszVal"));
            Assert.AreEqual(8, (int)Marshal.OffsetOf(typeof(PropVariant), "llVal"));
            Assert.AreEqual(8, (int)Marshal.OffsetOf(typeof(PropVariant), "dblVal"));
        }

        [TestMethod]
        public void PropVariant_GetString_OnEmpty_ReturnsNull()
        {
            var pv = new PropVariant();
            Assert.IsNull(pv.GetString());
        }

        [TestMethod]
        public void PropVariant_Clear_OnEmpty_DoesNotCallIntoNative()
        {
            // VT_EMPTY 无分配内容，不应调用 PropVariantClear（也确保不会被误用为释放未初始化内存）
            var pv = new PropVariant();
            pv.Clear();
            Assert.AreEqual(0, pv.vt);
        }

        /// <summary>
        /// ProcessJob 必须自带终结器。
        ///
        /// 作业句柄由 SafeJobHandle 承载，而 SafeHandle 只有在它的"拥有者"被 GC 回收时
        /// 才会终结 —— 也就是说 ProcessJob 被 GC 之前，句柄一直开着。若没有任何异常路径
        /// 漏掉 Dispose（典型如进程启动成功后 BeginOutputReadLine 抛异常），作业句柄就是
        /// 真泄漏，且 kill-on-close 兜底因为句柄永不关闭而彻底失效。
        /// </summary>
        [TestMethod]
        public void ProcessJob_HasFinalizer_SoJobHandleCannotLeak()
        {
            var finalize = typeof(ProcessJob).GetMethod("Finalize", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(finalize,
                "ProcessJob 必须声明终结器：SafeJobHandle 不会自行终结，漏调 Dispose 即句柄真泄漏");
        }

        /// <summary>正常路径的作业对象创建/释放不得抛异常，且重复 Dispose 幂等。</summary>
        [TestMethod]
        public void ProcessJob_CreateAndDispose_IsIdempotent()
        {
            var job = ProcessJob.TryCreate();
            try
            {
                Assert.IsNotNull(job, "本机应能创建作业对象（跳过则说明环境不支持）");
                job.Dispose();
                job.Dispose();
            }
            catch (Exception ex)
            {
                Assert.Fail("ProcessJob 释放不应抛异常: " + ex);
            }
        }
    }
}
