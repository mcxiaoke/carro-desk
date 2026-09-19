using System;
using System.IO;
using CarroDesk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 测试进程级环境隔离（P0-1）。
    ///
    /// 背景：<see cref="ConfigService"/> 的数据目录默认指向 %AppData%\CarroDesk，
    /// 而多个测试会直接 new ConfigService() 并落盘。若不隔离，运行一次测试就会
    /// 覆写开发者/用户真实的 config.json（已实测复现：测试夹具值被写入生产配置）。
    ///
    /// 在程序集初始化阶段把数据目录重定向到本次运行专属的临时目录，
    /// 使全部既有测试自动获得隔离，无需逐个改造。
    /// </summary>
    [TestClass]
    public class TestEnvironment
    {
        /// <summary>本次测试运行专属的数据目录（其余测试如需落盘应以此为根）。</summary>
        public static string TempRoot { get; private set; }

        [AssemblyInitialize]
        public static void AssemblyInit(TestContext context)
        {
            TempRoot = Path.Combine(
                Path.GetTempPath(),
                "CarroDesk.Tests",
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            Directory.CreateDirectory(TempRoot);
            ConfigService.DataDirOverride = TempRoot;

            context.WriteLine("测试数据目录已隔离至: " + TempRoot);
        }

        [AssemblyCleanup]
        public static void AssemblyCleanup()
        {
            ConfigService.DataDirOverride = null;

            try
            {
                if (!string.IsNullOrEmpty(TempRoot) && Directory.Exists(TempRoot))
                {
                    Directory.Delete(TempRoot, true);
                }
            }
            catch
            {
                // 临时目录清理失败不影响测试结论（残留目录由系统回收）
            }
        }
    }
}
