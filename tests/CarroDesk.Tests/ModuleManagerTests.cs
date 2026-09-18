using System;
using System.Collections.Generic;
using CarroDesk.Core;
using CarroDesk.Core.Models;
using CarroDesk.Host.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class ModuleManagerTests
    {
        private class DummyConfig { }

        private class TestDummyModule : ModuleBase<DummyConfig>
        {
            private readonly string _id;
            private readonly int _order;
            private readonly bool _throwOnInit;
            private readonly bool _throwOnStart;
            private readonly Action _onStart;
            private readonly Action _onStop;

            public TestDummyModule(
                string id,
                int order = 0,
                bool throwOnInit = false,
                bool throwOnStart = false,
                Action onStart = null,
                Action onStop = null)
            {
                _id = id;
                _order = order;
                _throwOnInit = throwOnInit;
                _throwOnStart = throwOnStart;
                _onStart = onStart;
                _onStop = onStop;
            }

            public override string Id => _id;
            public override string Name => _id;
            public override int Order => _order;

            public override void Initialize(IModuleContext context)
            {
                if (_throwOnInit)
                {
                    Status = ModuleStatus.Faulted;
                    throw new InvalidOperationException("Init crash");
                }
                base.Initialize(context);
            }

            protected override void OnStart()
            {
                if (_throwOnStart) throw new InvalidOperationException("Start crash");
                _onStart?.Invoke();
            }

            protected override void OnStop()
            {
                _onStop?.Invoke();
            }
            public override IEnumerable<TrayMenuItem> GetTrayMenuItems()
            {
                return new[] { new TrayMenuItem { Id = _id + "_item", Header = _id } };
            }
        }

        [TestMethod]
        public void RegisterModule_RejectsDuplicatesAndNull()
        {
            var manager = new ModuleManager();
            var mod1 = new TestDummyModule("mod_test");

            manager.RegisterModule(mod1);

            // Null registration throws ArgumentNullException
            Assert.ThrowsException<ArgumentNullException>(() => manager.RegisterModule(null));

            // Duplicate registration (case-insensitive) throws InvalidOperationException
            var modDup = new TestDummyModule("MOD_TEST");
            Assert.ThrowsException<InvalidOperationException>(() => manager.RegisterModule(modDup));

            Assert.AreEqual(1, manager.Modules.Count);
            Assert.AreSame(mod1, manager.GetModule("mod_test"));
            Assert.AreSame(mod1, manager.GetModule("MOD_TEST"));
        }

        [TestMethod]
        public void Lifecycle_StartsAndStopsInCorrectOrder()
        {
            var manager = new ModuleManager();
            var startOrder = new List<string>();
            var stopOrder = new List<string>();

            var mod1 = new TestDummyModule("mod1", order: 10, onStart: () => startOrder.Add("mod1"), onStop: () => stopOrder.Add("mod1"));
            var mod2 = new TestDummyModule("mod2", order: 20, onStart: () => startOrder.Add("mod2"), onStop: () => stopOrder.Add("mod2"));

            manager.RegisterModule(mod1);
            manager.RegisterModule(mod2);

            var services = new ServiceContainer();
            manager.InitializeAll(services);

            Assert.AreEqual(ModuleStatus.Initialized, mod1.Status);
            Assert.AreEqual(ModuleStatus.Initialized, mod2.Status);

            manager.StartAll();

            Assert.AreEqual(ModuleStatus.Running, mod1.Status);
            Assert.AreEqual(ModuleStatus.Running, mod2.Status);
            Assert.IsTrue(mod1.IsRunning);
            Assert.IsTrue(mod2.IsRunning);
            CollectionAssert.AreEqual(new[] { "mod1", "mod2" }, startOrder);

            manager.StopAll();

            Assert.AreEqual(ModuleStatus.Stopped, mod1.Status);
            Assert.AreEqual(ModuleStatus.Stopped, mod2.Status);
            Assert.IsFalse(mod1.IsRunning);
            Assert.IsFalse(mod2.IsRunning);
            // StopAll must stop in reverse order
            CollectionAssert.AreEqual(new[] { "mod2", "mod1" }, stopOrder);
        }

        [TestMethod]
        public void FaultIsolation_InitializeFailure_DoesNotCrashHostAndIsSkippedOnStart()
        {
            var manager = new ModuleManager();
            var faultyMod = new TestDummyModule("faulty", throwOnInit: true);
            var healthyMod = new TestDummyModule("healthy");

            manager.RegisterModule(faultyMod);
            manager.RegisterModule(healthyMod);

            var services = new ServiceContainer();

            // InitializeAll should not throw unhandled exception
            manager.InitializeAll(services);

            Assert.AreEqual(ModuleStatus.Faulted, faultyMod.Status);
            Assert.AreEqual(ModuleStatus.Initialized, healthyMod.Status);

            // StartAll should skip faulted module
            manager.StartAll();

            Assert.AreEqual(ModuleStatus.Faulted, faultyMod.Status);
            Assert.IsFalse(faultyMod.IsRunning);

            Assert.AreEqual(ModuleStatus.Running, healthyMod.Status);
            Assert.IsTrue(healthyMod.IsRunning);
        }

        [TestMethod]
        public void FaultIsolation_StartFailure_MarksFaultedAndDoesNotAffectOthers()
        {
            var manager = new ModuleManager();
            var faultyMod = new TestDummyModule("faulty", throwOnStart: true);
            var healthyMod = new TestDummyModule("healthy");

            manager.RegisterModule(faultyMod);
            manager.RegisterModule(healthyMod);

            var services = new ServiceContainer();
            manager.InitializeAll(services);

            manager.StartAll();

            Assert.AreEqual(ModuleStatus.Faulted, faultyMod.Status);
            Assert.IsFalse(faultyMod.IsRunning);

            Assert.AreEqual(ModuleStatus.Running, healthyMod.Status);
            Assert.IsTrue(healthyMod.IsRunning);
        }

        [TestMethod]
        public void FaultIsolation_FaultedAndUninitializedModules_ExcludedFromTrayMenuItems()
        {
            var manager = new ModuleManager();
            var faultyMod = new TestDummyModule("faulty", throwOnInit: true);
            var healthyMod = new TestDummyModule("healthy");

            manager.RegisterModule(faultyMod);
            manager.RegisterModule(healthyMod);

            var services = new ServiceContainer();
            manager.InitializeAll(services);

            var items = new List<TrayMenuItem>(manager.GetAllTrayMenuItems());
            Assert.AreEqual(1, items.Count);
            Assert.AreEqual("healthy_item", items[0].Id);
        }

        [TestMethod]
        public void ModuleConfig_DirectSerialization_And_RoundTrip()
        {
            var configService = new CarroDesk.Services.ConfigService();
            configService.LoadOrCreate();

            var configMgr = new ConfigManager(configService);
            var services = new ServiceContainer();
            services.AddSingleton<IConfigManager>(configMgr);

            var manager = new ModuleManager();
            var screenLock = new CarroDesk.Modules.ScreenLock.ScreenLockModule();
            var taskScheduler = new CarroDesk.Modules.TaskScheduler.TaskSchedulerModule();

            manager.RegisterModule(screenLock);
            manager.RegisterModule(taskScheduler);

            manager.InitializeAll(services);

            // 1. 验证 ScreenLockConfig 动态读取默认值
            var slConfig = configMgr.GetModuleConfig<CarroDesk.Modules.ScreenLock.Models.ScreenLockConfig>("ScreenLock");
            Assert.AreEqual(5, slConfig.IdleMinutes);

            // 2. 验证 ScreenLockConfig 动态更新写入模块节，且不污染 Host AppSettings
            slConfig.IdleMinutes = 12;
            configMgr.SaveModuleConfig("ScreenLock", slConfig);
            var reloadedSl = configMgr.GetModuleConfig<CarroDesk.Modules.ScreenLock.Models.ScreenLockConfig>("ScreenLock");
            Assert.AreEqual(12, reloadedSl.IdleMinutes);
            Assert.IsNotNull(configService.GetModuleToken("ScreenLock"));

            // 3. 验证 TaskSchedulerConfig 动态读取与写入
            var tsConfig = configMgr.GetModuleConfig<CarroDesk.Modules.TaskScheduler.Models.TaskSchedulerConfig>("TaskScheduler");
            Assert.IsTrue(tsConfig.GlobalEnabled);

            tsConfig.GlobalEnabled = false;
            configMgr.SaveModuleConfig("TaskScheduler", tsConfig);
            var reloadedTs = configMgr.GetModuleConfig<CarroDesk.Modules.TaskScheduler.Models.TaskSchedulerConfig>("TaskScheduler");
            Assert.IsFalse(reloadedTs.GlobalEnabled);
            Assert.IsNotNull(configService.GetModuleToken("TaskScheduler"));
        }
    }
}
