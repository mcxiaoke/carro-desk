using System;
using CarroDesk.Core;
using CarroDesk.Services.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CarroDesk.Tests
{
    [TestClass]
    public class TaskStateMachineTests
    {
        [TestMethod]
        public void NormalRun_SuccessCycle_TransitionsCorrectly()
        {
            var fsm = new TaskStateMachine("test-task");
            Assert.AreEqual(TaskRuntimeState.Idle, fsm.CurrentState);

            // 1. TryBeginStarting
            Assert.IsTrue(fsm.TryBeginStarting(false, out var refusal));
            Assert.IsNull(refusal);
            Assert.AreEqual(TaskRuntimeState.Starting, fsm.CurrentState);

            // 2. OnProcessStarted
            var startTime = DateTime.Now;
            fsm.OnProcessStarted(1234, startTime);
            Assert.AreEqual(TaskRuntimeState.Running, fsm.CurrentState);
            Assert.AreEqual(1234, fsm.CurrentPid);

            // 3. OnProcessExited (0)
            var state = fsm.OnProcessExited(0, false, false, 0, 0, 0);
            Assert.AreEqual(TaskRuntimeState.Idle, state);
            Assert.AreEqual(TaskRuntimeState.Idle, fsm.CurrentState);
            Assert.IsNull(fsm.CurrentPid);
            Assert.AreEqual(0, fsm.LastExitCode);
            Assert.AreEqual(0, fsm.ConsecutiveFailures);
        }

        [TestMethod]
        public void DetachFailure_WithRetry_TransitionsToWaitingRetryAndMarkedFailed()
        {
            var fsm = new TaskStateMachine("supervise-task");

            // Attempt 1: fails
            Assert.IsTrue(fsm.TryBeginStarting(false, out _));
            fsm.OnProcessStarted(2001, DateTime.Now);
            var state = fsm.OnProcessExited(1, false, true, 5, 2, 60);
            Assert.AreEqual(TaskRuntimeState.WaitingRetry, state);
            Assert.AreEqual(1, fsm.ConsecutiveFailures);
            Assert.AreEqual(5, fsm.NextRetryDelaySec);

            // Attempt 2: fails. 守护重启链（RestartContinue）不经 TryBeginStarting 直接
            // OnProcessStarted——延迟窗口内的非人工 TryBeginStarting 已被拒绝（M2 修复），
            // 此处按真实序列模拟。
            fsm.OnProcessStarted(2002, DateTime.Now);
            state = fsm.OnProcessExited(1, false, true, 5, 2, 60);
            Assert.AreEqual(TaskRuntimeState.WaitingRetry, state);
            Assert.AreEqual(2, fsm.ConsecutiveFailures);

            // Attempt 3: failures reach 3 > limit (2) -> MarkedFailed
            fsm.OnProcessStarted(2003, DateTime.Now);
            state = fsm.OnProcessExited(1, false, true, 5, 2, 60);
            Assert.AreEqual(TaskRuntimeState.MarkedFailed, state);
            Assert.AreEqual(3, fsm.ConsecutiveFailures);
            Assert.IsNotNull(fsm.MarkedFailedAt);

            // Non user-driven start is refused
            Assert.IsFalse(fsm.TryBeginStarting(false, out var refusal));
            Assert.IsTrue(refusal.Contains("熔断"));

            // User-driven start succeeds and resets failure count
            Assert.IsTrue(fsm.TryBeginStarting(true, out _));
            Assert.AreEqual(0, fsm.ConsecutiveFailures);
            Assert.IsNull(fsm.MarkedFailedAt);
        }

        [TestMethod]
        public void RequestStop_WhileWaitingRetry_CancelsRetryImmediately()
        {
            var fsm = new TaskStateMachine("retry-task");
            Assert.IsTrue(fsm.TryBeginStarting(false, out _));
            fsm.OnProcessStarted(3001, DateTime.Now);
            fsm.OnProcessExited(1, false, true, 5, 3, 60);
            Assert.AreEqual(TaskRuntimeState.WaitingRetry, fsm.CurrentState);

            // User clicks stop during retry countdown
            bool ok = fsm.RequestStop(out bool cancelledPendingRetry);
            Assert.IsTrue(ok);
            Assert.IsTrue(cancelledPendingRetry);
            Assert.AreEqual(TaskRuntimeState.Idle, fsm.CurrentState);
            Assert.AreEqual(0, fsm.NextRetryDelaySec);
        }

        [TestMethod]
        public void RequestStop_WhileRunning_TransitionsToStoppingThenIdle()
        {
            var fsm = new TaskStateMachine("run-task");
            Assert.IsTrue(fsm.TryBeginStarting(false, out _));
            fsm.OnProcessStarted(4001, DateTime.Now);
            Assert.AreEqual(TaskRuntimeState.Running, fsm.CurrentState);

            bool ok = fsm.RequestStop(out bool cancelledPendingRetry);
            Assert.IsTrue(ok);
            Assert.IsFalse(cancelledPendingRetry);
            Assert.AreEqual(TaskRuntimeState.Stopping, fsm.CurrentState);

            fsm.OnStopCompleted();
            Assert.AreEqual(TaskRuntimeState.Idle, fsm.CurrentState);
        }

        [TestMethod]
        public void StableUptime_ResetsConsecutiveFailures()
        {
            var fsm = new TaskStateMachine("stable-task");
            // First failure
            Assert.IsTrue(fsm.TryBeginStarting(false, out _));
            fsm.OnProcessStarted(5001, DateTime.Now);
            fsm.OnProcessExited(1, false, true, 5, 5, 60);
            Assert.AreEqual(1, fsm.ConsecutiveFailures);

            // Second run ran for 100 seconds (stable >= 60). 守护重启链不经 TryBeginStarting
            //（延迟窗口内的非人工 TryBeginStarting 已被 M2 修复拒绝），按真实序列直接 OnProcessStarted。
            fsm.OnProcessStarted(5002, DateTime.Now.AddSeconds(-100));
            // Exited with error, but because duration >= 60s, history failures reset
            fsm.OnProcessExited(1, false, true, 5, 5, 60);
            // It reset to 0, then incremented to 1 for this new failure
            Assert.AreEqual(1, fsm.ConsecutiveFailures);
        }

        [TestMethod]
        public void Snapshot_CapturesStatePropertiesAccurately()
        {
            var fsm = new TaskStateMachine("snap-task");
            var snap1 = fsm.GetSnapshot();
            Assert.AreEqual("snap-task", snap1.TaskName);
            Assert.AreEqual(TaskRuntimeState.Idle, snap1.State);
            Assert.IsFalse(snap1.IsRunning);

            Assert.IsTrue(fsm.TryBeginStarting(false, out _));
            fsm.OnProcessStarted(6001, DateTime.Now);
            var snap2 = fsm.GetSnapshot();
            Assert.AreEqual(TaskRuntimeState.Running, snap2.State);
            Assert.AreEqual(6001, snap2.Pid);
            Assert.IsTrue(snap2.IsRunning);
        }

        // ===== M1/M2（docs/CODE-REVIEW-20261025.md）回归 =====

        /// <summary>
        /// 放弃启动不得复用 OnProcessExited(0)：会把并发实例计数减掉、状态谎报为空闲。
        /// </summary>
        [TestMethod]
        public void OnAbandon_KeepsRunningInstanceCountAndState()
        {
            var fsm = new TaskStateMachine("abandon-task");
            // 第一实例真正运行中
            Assert.IsTrue(fsm.TryBeginStarting(false, allowConcurrent: true, out _));
            fsm.OnProcessStarted(7001, DateTime.Now);
            Assert.AreEqual(TaskRuntimeState.Running, fsm.CurrentState);

            // 第二次触发（allowConcurrent）进入 Starting 语义后被放弃：
            // TryBeginStarting 在既有 Running 时保持 Running，放弃路径不应改动状态与计数
            Assert.IsTrue(fsm.TryBeginStarting(true, allowConcurrent: true, out _));
            fsm.OnAbandon();

            var snap = fsm.GetSnapshot();
            Assert.AreEqual(TaskRuntimeState.Running, snap.State, "既有实例仍在运行，放弃启动不得改变状态");
            Assert.AreEqual(1, snap.ActiveInstances, "放弃启动不得递减 ActiveInstances");
            Assert.AreEqual(0, snap.ConsecutiveFailures);
        }

        [TestMethod]
        public void OnAbandon_FromStarting_ReturnsToIdle()
        {
            var fsm = new TaskStateMachine("abandon-starting");
            Assert.IsTrue(fsm.TryBeginStarting(false, out _));
            Assert.AreEqual(TaskRuntimeState.Starting, fsm.CurrentState);

            fsm.OnAbandon();
            Assert.AreEqual(TaskRuntimeState.Idle, fsm.CurrentState);
            Assert.AreEqual(0, fsm.GetSnapshot().ActiveInstances);
        }

        /// <summary>
        /// WaitingRetry 延迟窗口内的非人工触发必须被拒绝：否则随后的放弃启动路径
        /// 会清零连续失败计数，架空 RestartLimit 熔断预算。
        /// </summary>
        [TestMethod]
        public void TryBeginStarting_RejectsNonUserDrivenWhileWaitingRetry()
        {
            var fsm = new TaskStateMachine("waiting-retry-task");
            // 制造 WaitingRetry：失败 1 次（预算 2）
            Assert.IsTrue(fsm.TryBeginStarting(false, out _));
            fsm.OnProcessStarted(8001, DateTime.Now);
            var state = fsm.OnProcessExited(1, false, true, 5, 2, 0);
            Assert.AreEqual(TaskRuntimeState.WaitingRetry, state);

            // 非人工触发在延迟窗口内被拒
            Assert.IsFalse(fsm.TryBeginStarting(false, out var refusal));
            StringAssert.Contains(refusal, "延迟", "拒绝理由应说明处于守护重启延迟期");

            // 计数未被冲刷
            Assert.AreEqual(1, fsm.ConsecutiveFailures, "被拒触发不得清零连续失败计数");

            // 人工触发仍可放行（显式意图）
            Assert.IsTrue(fsm.TryBeginStarting(true, out _));
        }

        // ===== S1 / H4（docs/OPEN-ISSUES-20261110.md）回归 =====

        /// <summary>
        /// 同一次执行内的重试尝试不得重复累加实例计数：每轮都 OnProcessStarted 而退出只减一次，
        /// 会让任务重试结束后永久停在"假运行"状态（S1）。
        /// </summary>
        [TestMethod]
        public void OnAttemptStarted_RetryAttempt_DoesNotAccumulateInstances()
        {
            var fsm = new TaskStateMachine("retry-count");
            Assert.IsTrue(fsm.TryBeginStarting(false, out _));
            fsm.OnProcessStarted(9001, DateTime.Now);
            Assert.AreEqual(1, fsm.GetSnapshot().ActiveInstances);

            // 第一次尝试失败后，同一执行内发起第二次尝试
            fsm.OnAttemptStarted(9002, DateTime.Now);
            var snap = fsm.GetSnapshot();
            Assert.AreEqual(1, snap.ActiveInstances, "重试不得累加 ActiveInstances");
            Assert.AreEqual(9002, snap.Pid, "重试应更新当前 pid");
            Assert.AreEqual(TaskRuntimeState.Running, snap.State);

            // 执行结束只减一次，计数归零、状态回 Idle
            fsm.OnProcessExited(0, false, false, 0, 0, 0);
            Assert.AreEqual(0, fsm.GetSnapshot().ActiveInstances);
            Assert.AreEqual(TaskRuntimeState.Idle, fsm.CurrentState);
        }

        /// <summary>
        /// WaitingRetry 下放弃重启（单实例被占用/启动异常/重载）必须回到 Idle，
        /// 否则状态机永久卡死、后续触发全被拒绝（H4）。
        /// </summary>
        [TestMethod]
        public void OnAbandon_FromWaitingRetry_ReturnsToIdleAndKeepsFailures()
        {
            var fsm = new TaskStateMachine("abandon-retry");
            Assert.IsTrue(fsm.TryBeginStarting(false, out _));
            fsm.OnProcessStarted(9101, DateTime.Now);
            var state = fsm.OnProcessExited(1, false, true, 5, 2, 0);
            Assert.AreEqual(TaskRuntimeState.WaitingRetry, state);
            Assert.AreEqual(1, fsm.ConsecutiveFailures);

            fsm.OnAbandon();

            Assert.AreEqual(TaskRuntimeState.Idle, fsm.CurrentState);
            Assert.AreEqual(0, fsm.NextRetryDelaySec, "放弃重试必须取消倒计时");
            Assert.AreEqual(1, fsm.ConsecutiveFailures, "放弃启动不应清空失败预算");
            // 放弃后非人工触发应可再次进入 Starting（不再被 WaitingRetry 拒绝）
            Assert.IsTrue(fsm.TryBeginStarting(false, out _));
        }

        /// <summary>编辑保存/配置重载的 Reset 必须能取消 WaitingRetry 的待重试。</summary>
        [TestMethod]
        public void Reset_FromWaitingRetry_CancelsPendingRetry()
        {
            var fsm = new TaskStateMachine("reset-retry");
            Assert.IsTrue(fsm.TryBeginStarting(false, out _));
            fsm.OnProcessStarted(9201, DateTime.Now);
            fsm.OnProcessExited(1, false, true, 5, 2, 0);
            Assert.AreEqual(TaskRuntimeState.WaitingRetry, fsm.CurrentState);

            fsm.Reset();

            Assert.AreEqual(TaskRuntimeState.Idle, fsm.CurrentState);
            Assert.AreEqual(0, fsm.NextRetryDelaySec);
            Assert.AreEqual(0, fsm.ConsecutiveFailures);
        }
    }
}
