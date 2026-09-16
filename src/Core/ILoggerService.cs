using System;

namespace CarroDesk.Core
{
    public interface ILoggerService
    {
        void LogInfo(string module, string message);
        void LogWarning(string module, string message);
        void LogError(string module, string message, Exception ex = null);
    }
}
