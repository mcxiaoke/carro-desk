using System;
using System.Runtime.InteropServices;
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
    }
}
