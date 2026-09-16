using System;
using CarroDesk.Models;

namespace CarroDesk.Services.Tasks.Triggers
{
    public interface ITrigger : IDisposable
    {
        TaskDefinition Task { get; }
        void Start();
        void Stop();
        event Action<TaskDefinition, string> Fired;
    }
}
