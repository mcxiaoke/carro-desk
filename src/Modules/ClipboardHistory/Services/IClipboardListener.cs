using System;

namespace CarroDesk.Modules.ClipboardHistory.Services
{
    public interface IClipboardListener : IDisposable
    {
        event Action<string> ClipboardUpdated;

        bool IsListening { get; }

        void Start();

        void Stop();
    }
}
