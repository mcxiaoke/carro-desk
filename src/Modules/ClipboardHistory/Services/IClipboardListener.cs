using System;

namespace CarroDesk.Modules.ClipboardHistory.Services
{
    public interface IClipboardListener : IDisposable
    {
        event Action ClipboardUpdated;

        bool IsListening { get; }

        void Start();

        void Stop();
    }
}
