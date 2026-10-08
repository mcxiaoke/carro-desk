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

            // Attempt 2: fails
            Assert.IsTrue(fsm.TryBeginStarting(false, out _));
            fsm.OnProcessStarted(2002, DateTime.Now);
            state = fsm.OnProcessExited(1, false, true, 5, 2, 60);
            Assert.AreEqual(TaskRuntimeState.WaitingRetry, state);
            Assert.AreEqual(2, fsm.ConsecutiveFailures);

            // Attempt 3: failures reach 3 > limit (2) -> MarkedFailed
            Assert.IsTrue(fsm.TryBeginStarting(false, out _));
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

            // Second run ran for 100 seconds (stable >= 60)
            Assert.IsTrue(fsm.TryBeginStarting(false, out _));
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
    }
}
