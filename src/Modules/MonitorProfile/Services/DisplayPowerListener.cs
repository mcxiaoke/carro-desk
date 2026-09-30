using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using CarroDesk.Core;

namespace CarroDesk.Modules.MonitorProfile.Services
{
    /// <summary>
    /// Windows 显示器电源状态监听器。
    /// 基于 Windows 原生 API RegisterPowerSettingNotification 监听 GUID_CONSOLE_DISPLAY_STATE 与 GUID_MONITOR_POWER_ON，
    /// 精准捕捉显示器亮屏、熄灭与休眠事件，零轮询零额外功耗。
    /// </summary>
    public class DisplayPowerListener : IDisplayPowerListener
    {
        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;
        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

        // Windows 电源管理标准 GUID
        private static Guid GUID_CONSOLE_DISPLAY_STATE = new Guid("da11facf-97e0-4e4f-a3db-4e6ea5b2fba7");
        private static Guid GUID_MONITOR_POWER_ON = new Guid("02731015-4510-4526-99e6-9698041e1d33");

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, uint Flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct POWERBROADCAST_SETTING
        {
            public Guid PowerSetting;
            public uint DataLength;
            // 之后紧跟实际数据，通过内存偏移读取
        }

        private readonly object _lock = new object();
        private readonly Dispatcher _dispatcher;
        private readonly ILoggerService _logger;

        private HwndSource _hwndSource;
        private IntPtr _hConsoleDisplayNotify = IntPtr.Zero;
        private IntPtr _hMonitorPowerNotify = IntPtr.Zero;
        private volatile bool _isDisplayOn = true;
        private bool _isListening;
        private bool _disposed;

        public bool IsDisplayOn => _isDisplayOn;

        public event Action<bool> DisplayPowerChanged;

        public DisplayPowerListener(Dispatcher dispatcher = null, ILoggerService logger = null)
        {
            _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
            _logger = logger;
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_disposed || _isListening) return;

                void InitCore()
                {
                    try
                    {
                        if (_hwndSource == null)
                        {
                            var parameters = new HwndSourceParameters("CarroDesk_DisplayPowerListener")
                            {
                                Width = 0,
                                Height = 0,
                                WindowStyle = 0
                            };
                            _hwndSource = new HwndSource(parameters);
                            _hwndSource.AddHook(WndProc);
                        }

                        IntPtr hwnd = _hwndSource.Handle;
                        if (hwnd != IntPtr.Zero)
                        {
                            _hConsoleDisplayNotify = RegisterPowerSettingNotification(hwnd, ref GUID_CONSOLE_DISPLAY_STATE, DEVICE_NOTIFY_WINDOW_HANDLE);
                            _hMonitorPowerNotify = RegisterPowerSettingNotification(hwnd, ref GUID_MONITOR_POWER_ON, DEVICE_NOTIFY_WINDOW_HANDLE);
                            _isListening = true;
                            _logger?.LogInfo("DisplayPower", "已成功注册显示器电源状态监听 (RegisterPowerSettingNotification)");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning("DisplayPower", "注册显示器电源设置监听失败: " + ex.Message);
                        _isListening = false;
                    }
                }

                if (_dispatcher.CheckAccess())
                {
                    InitCore();
                }
                else
                {
                    _dispatcher.Invoke(InitCore);
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_isListening) return;

                void CleanupCore()
                {
                    try
                    {
                        if (_hConsoleDisplayNotify != IntPtr.Zero)
                        {
                            UnregisterPowerSettingNotification(_hConsoleDisplayNotify);
                            _hConsoleDisplayNotify = IntPtr.Zero;
                        }

                        if (_hMonitorPowerNotify != IntPtr.Zero)
                        {
                            UnregisterPowerSettingNotification(_hMonitorPowerNotify);
                            _hMonitorPowerNotify = IntPtr.Zero;
                        }

                        if (_hwndSource != null)
                        {
                            _hwndSource.RemoveHook(WndProc);
                            _hwndSource.Dispose();
                            _hwndSource = null;
                        }
                    }
                    catch { }
                    finally
                    {
                        _isListening = false;
                    }
                }

                if (_dispatcher.CheckAccess())
                {
                    CleanupCore();
                }
                else
                {
                    try { _dispatcher.Invoke(CleanupCore); } catch { }
                }
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
            {
                try
                {
                    var setting = (POWERBROADCAST_SETTING)Marshal.PtrToStructure(lParam, typeof(POWERBROADCAST_SETTING));
                    IntPtr pData = new IntPtr(lParam.ToInt64() + 20); // Guid(16) + DataLength(4) = 20

                    if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
                    {
                        int state = Marshal.ReadInt32(pData);
                        // 0 = Off, 1 = On, 2 = Dim
                        bool isOn = (state == 1);
                        UpdateState(isOn, $"CONSOLE_DISPLAY_STATE={state}");
                    }
                    else if (setting.PowerSetting == GUID_MONITOR_POWER_ON)
                    {
                        int state = Marshal.ReadInt32(pData);
                        bool isOn = (state != 0);
                        UpdateState(isOn, $"MONITOR_POWER_ON={state}");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError("DisplayPower", "解析电源广播设置消息异常", ex);
                }
            }

            return IntPtr.Zero;
        }

        private void UpdateState(bool isOn, string reason)
        {
            if (_isDisplayOn != isOn)
            {
                _isDisplayOn = isOn;
                _logger?.LogInfo("DisplayPower", $"显示器电源状态变更: {(isOn ? "开启(亮屏)" : "关闭/休眠")} ({reason})");
                DisplayPowerChanged?.Invoke(isOn);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                Stop();
            }
        }
    }
}
