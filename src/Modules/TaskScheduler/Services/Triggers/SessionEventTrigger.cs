using System;
using Microsoft.Win32;
using CarroDesk.Common;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks.Triggers
{
    public class SessionEventTrigger : ITrigger
    {
        public TaskDefinition Task { get; private set; }
        public event Action<TaskDefinition, string> Fired;
        private readonly TaskTriggerType _type;
        private bool _subscribed;

        public SessionEventTrigger(TaskDefinition task)
        {
            Task = task;
            _type = task.Trigger.Type;
        }

        public void Start()
        {
            if (_subscribed) return;
            try
            {
                SystemEvents.SessionSwitch += OnSessionSwitch;
                _subscribed = true;
            }
            catch (Exception ex)
            {
                // 订阅失败会让 sessionLock / sessionUnlock 任务永不触发且毫无线索
                StaticLog.Warn("TaskScheduler", "订阅 SystemEvents.SessionSwitch 失败，会话锁定/解锁触发将不生效: " + ex.Message);
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            try
            {
                if (_type == TaskTriggerType.SessionLock && e.Reason == SessionSwitchReason.SessionLock)
                {
                    var h = Fired;
                    if (h != null) h(Task, "sessionLock");
                }
                else if (_type == TaskTriggerType.SessionUnlock && e.Reason == SessionSwitchReason.SessionUnlock)
                {
                    var h = Fired;
                    if (h != null) h(Task, "sessionUnlock");
                }
            }
            catch (Exception ex)
            {
                StaticLog.Warn("TaskScheduler", "会话事件触发回调异常: " + ex.Message);
            }
        }

        public void Stop()
        {
            if (!_subscribed) return;
            try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
            _subscribed = false;
        }

        public void Dispose() { Stop(); }
    }
}
