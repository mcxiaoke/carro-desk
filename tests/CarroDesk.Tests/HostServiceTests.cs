using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CarroDesk.Host.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    /// <summary>
    /// 宿主服务的解析与超时语义测试（P2-14 / P2-15）。
    /// </summary>
    [TestClass]
    public class HostServiceTests
    {
        // ---------- P2-14 定向解析 ----------

        [TestMethod]
        public void ServiceContainer_GetService_InstantiatesOnlyFirstRegistration()
        {
            using (var container = new ServiceContainer())
            {
                int constructed = 0;

                for (int i = 0; i < 3; i++)
                {
                    var index = i;
                    container.AddSingleton<IServiceStub>(_ =>
                    {
                        Interlocked.Increment(ref constructed);
                        return new ServiceStub("svc-" + index);
                    });
                }

                var resolved = container.GetService<IServiceStub>();

                Assert.IsNotNull(resolved);
                Assert.AreEqual("svc-0", resolved.Name, "应解析第一个注册");
                Assert.AreEqual(1, constructed,
                    "GetService 不应把该类型下所有注册都实例化一遍（此前用 ResolveAll 再取 [0]）");
            }
        }

        [TestMethod]
        public void ServiceContainer_GetServices_InstantiatesAllRegistrations()
        {
            using (var container = new ServiceContainer())
            {
                int constructed = 0;
                for (int i = 0; i < 3; i++)
                {
                    container.AddSingleton<IServiceStub>(_ =>
                    {
                        Interlocked.Increment(ref constructed);
                        return new ServiceStub("svc-" + i);
                    });
                }

                var all = container.GetServices<IServiceStub>();

                Assert.AreEqual(3, all.Count, "GetServices 语义就是解析全部");
                Assert.AreEqual(3, constructed);
            }
        }

        [TestMethod]
        public void ServiceContainer_TryGetService_MatchesGetService()
        {
            using (var container = new ServiceContainer())
            {
                container.AddSingleton<IServiceStub>(new ServiceStub("a"));

                // 注册时以声明的服务类型为键，因此这里必须用接口类型解析
                IServiceStub hit;
                Assert.IsTrue(container.TryGetService(out hit));
                Assert.AreEqual("a", hit.Name);

                IServiceStub2 unregistered;
                Assert.IsFalse(container.TryGetService(out unregistered), "未注册的类型不应解析成功");
            }
        }

        [TestMethod]
        public void ServiceContainer_Dispose_DisposesSharedInstanceOnlyOnce()
        {
            var shared = new ServiceStub("shared");
            var container = new ServiceContainer();

            // 同一实例注册到两个类型：释放时不得重复 Dispose
            container.AddSingleton<IServiceStub>(shared);
            container.AddSingleton<IServiceStub2>(shared);

            container.Dispose();

            Assert.AreEqual(1, shared.DisposeCount, "同一实例被释放了多次");
        }

        // ---------- P2-15 异步退出守卫 ----------

        [TestMethod]
        public async Task SafeInvoker_RunTimeoutAsync_CompletesAndPropagatesResult()
        {
            bool invoked = false;
            bool ok = await SafeInvoker.RunTimeoutAsync("t", TimeSpan.FromSeconds(3), () => invoked = true);

            Assert.IsTrue(ok);
            Assert.IsTrue(invoked);
        }

        [TestMethod]
        public async Task SafeInvoker_RunTimeoutAsync_TimesOutWithoutBlocking_AndReportsError()
        {
            var errors = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            bool ok = await SafeInvoker.RunTimeoutAsync("t", TimeSpan.FromMilliseconds(200),
                () => Thread.Sleep(TimeSpan.FromSeconds(5)),
                (id, ex) => errors.Add(id + ":" + ex.GetType().Name));

            sw.Stop();

            Assert.IsFalse(ok, "超时应视为放行（返回 false）");
            Assert.AreEqual(1, errors.Count, "超时必须留痕");
            Assert.IsTrue(errors[0].Contains("TimeoutException"), "超时应以 TimeoutException 记录，实际: " + errors[0]);
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(3), "不得同步阻塞等待 action 跑完，实际耗时 " + sw.Elapsed);
        }

        [TestMethod]
        public async Task SafeInvoker_RunTimeoutAsync_ActionThrows_IsReportedAsFailure()
        {
            var errors = new List<string>();

            bool ok = await SafeInvoker.RunTimeoutAsync("t", TimeSpan.FromSeconds(3),
                () => { throw new InvalidOperationException("boom"); },
                (id, ex) => errors.Add(ex.Message));

            Assert.IsFalse(ok);
            Assert.AreEqual(1, errors.Count);
            Assert.IsTrue(errors[0].Contains("boom"));
        }

        [TestMethod]
        public async Task SafeInvoker_RunTimeoutAsync_NullAction_ReturnsTrueImmediately()
        {
            bool ok = await SafeInvoker.RunTimeoutAsync("t", TimeSpan.FromSeconds(1), null);
            Assert.IsTrue(ok);
        }

        private interface IServiceStub
        {
            string Name { get; }
        }

        private interface IServiceStub2
        {
        }

        private sealed class ServiceStub : IServiceStub, IServiceStub2, IDisposable
        {
            public ServiceStub(string name) { Name = name; }

            public string Name { get; }
            public int DisposeCount { get; private set; }

            public void Dispose()
            {
                DisposeCount++;
            }
        }
    }
}
