using System;
using System.IO;
using System.Threading;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class SingleInstanceTests
    {
        [TestMethod]
        public void GetDataDirectoryHash_IsDeterministic_AndCaseInsensitive()
        {
            var origOverride = ConfigService.DataDirOverride;
            try
            {
                ConfigService.DataDirOverride = @"C:\TestDir\AppData";
                var hash1 = ConfigService.GetDataDirectoryHash();

                ConfigService.DataDirOverride = @"c:\testdir\appdata";
                var hash2 = ConfigService.GetDataDirectoryHash();

                Assert.IsNotNull(hash1);
                Assert.AreEqual(8, hash1.Length, "Hash 必须是 8 位大写十六进制字符");
                Assert.AreEqual(hash1, hash2, "路径大小写不同时，归一化哈希必须一致");
            }
            finally
            {
                ConfigService.DataDirOverride = origOverride;
            }
        }

        [TestMethod]
        public void DifferentDataDirectories_ProduceDifferentHashesAndMutexNames()
        {
            var origOverride = ConfigService.DataDirOverride;
            try
            {
                ConfigService.DataDirOverride = @"C:\Workspace1\AppData";
                var mutex1 = ConfigService.InstanceMutexName;
                var msg1 = ConfigService.ActivateMessageName;

                ConfigService.DataDirOverride = @"C:\Workspace2\AppData";
                var mutex2 = ConfigService.InstanceMutexName;
                var msg2 = ConfigService.ActivateMessageName;

                Assert.AreNotEqual(mutex1, mutex2, "不同数据目录必须派生不同的 Mutex 名称");
                Assert.AreNotEqual(msg1, msg2, "不同数据目录必须派生不同的激活消息名称");
                StringAssert.StartsWith(mutex1, @"Local\CarroDesk_");
                StringAssert.StartsWith(msg1, "CarroDesk_Activate_");
            }
            finally
            {
                ConfigService.DataDirOverride = origOverride;
            }
        }

        [TestMethod]
        public void SingleInstanceMutex_AcquiresAndBlocksSecondInstance()
        {
            var origOverride = ConfigService.DataDirOverride;
            var testTempDir = Path.Combine(Path.GetTempPath(), "CarroDesk_MutexTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(testTempDir);
                ConfigService.DataDirOverride = testTempDir;

                var mutexName = ConfigService.InstanceMutexName;

                // 第一个实例创建并持有
                using (var mutex1 = new Mutex(true, mutexName, out bool isNew1))
                {
                    Assert.IsTrue(isNew1, "首实例必须成功获取互斥体");

                    // 第二个实例尝试获取（在独立线程执行，避免同线程所有权重入误判）
                    bool isNew2 = true;
                    bool acquiredOnOtherThread = false;
                    var t = new Thread(() =>
                    {
                        using (var mutex2 = new Mutex(false, mutexName, out isNew2))
                        {
                            try
                            {
                                acquiredOnOtherThread = mutex2.WaitOne(0);
                            }
                            catch (AbandonedMutexException)
                            {
                                acquiredOnOtherThread = true;
                            }
                        }
                    });
                    t.Start();
                    t.Join();

                    Assert.IsFalse(isNew2, "既有互斥体存在时 isNew 必须为 false");
                    Assert.IsFalse(acquiredOnOtherThread, "第二实例不能在第一实例持有期间获取 Mutex");
                }

                // 第一实例释放后，后续实例应能重新获取
                using (var mutex3 = new Mutex(true, mutexName, out bool isNew3))
                {
                    bool acquired = isNew3 || mutex3.WaitOne(100);
                    Assert.IsTrue(acquired, "前持有者释放后，新实例应能成功获取");
                    try { mutex3.ReleaseMutex(); } catch { }
                }
            }
            finally
            {
                ConfigService.DataDirOverride = origOverride;
                try
                {
                    if (Directory.Exists(testTempDir)) Directory.Delete(testTempDir, true);
                }
                catch { }
            }
        }

        [TestMethod]
        public void IsSameApplicationWindow_ExcludesCurrentProcessAndZeroHwnd()
        {
            int currentPid = Environment.ProcessId;
            string currentProcName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

            // PID 等于自身时不应匹配
            bool selfMatched = NativeMethods.IsSameApplicationWindow(IntPtr.Zero, currentProcName, currentPid);
            Assert.IsFalse(selfMatched, "不能将自身或无效窗口判定为目标外部应用窗口");
        }

        [TestMethod]
        public void AbandonedMutex_CanBeSafelyAcquiredAndReleased()
        {
            var testMutexName = @"Local\CarroDesk_AbandonedTest_" + Guid.NewGuid().ToString("N");

            // 1. 在独立线程中持有 Mutex 并异常退出（不释放），制造 abandoned 状态
            var t = new Thread(() =>
            {
                var m = new Mutex(true, testMutexName);
                // 故意不调用 ReleaseMutex，线程退出使互斥体进入 Abandoned
            });
            t.Start();
            t.Join();

            // 2. 当前线程使用两阶段方式打开并获取
            Mutex acquiredMutex = null;
            bool gotLock = false;
            try
            {
                var m = new Mutex(false, testMutexName);
                acquiredMutex = m;
                try
                {
                    gotLock = m.WaitOne(100);
                }
                catch (AbandonedMutexException)
                {
                    gotLock = true;
                }

                Assert.IsTrue(gotLock, "前持有线程终止后，新实例必须能安全接手锁");
                Assert.IsNotNull(acquiredMutex, "两阶段构造必须保证 Mutex 实例引用不丢失");

                // 3. 验证获得所有权后可以正常释放，不抛出异常
                acquiredMutex.ReleaseMutex();
            }
            finally
            {
                acquiredMutex?.Dispose();
            }
        }
    }
}
