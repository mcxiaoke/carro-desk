using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using CarroDesk.Core;
using CarroDesk.Core.Audio;
using CarroDesk.Core.Models;

namespace CarroDesk.Host.Services
{
    public class AudioService : IAudioService, IDisposable
    {
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVNODES_CHANGED = 0x0007;
        private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
        private const uint DEVICE_NOTIFY_ALL_CLASSES = 0x00000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct DEV_BROADCAST_DEVICEINTERFACE_W
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public int dbcc_reserved;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr filter, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool UnregisterDeviceNotification(IntPtr hFilter);

        private IMMDeviceEnumerator _deviceEnumerator;

        /// <summary>
        /// 默认端点变化去抖：WM_DEVICECHANGE / DBT_DEVNODES_CHANGED 往往连续到达多次
        /// （一次插拔会广播一串事件），逐次触发会让托盘菜单连续重建。
        /// </summary>
        private static readonly TimeSpan DeviceChangeDebounce = TimeSpan.FromMilliseconds(400);

        private readonly object _deviceWatchLock = new object();
        private Dispatcher _dispatcher;
        private HwndSource _deviceNotifyWindow;
        private IntPtr _deviceNotifyHandle;
        private bool _deviceNotifyStarted;
        private DispatcherTimer _deviceChangeDebounceTimer;
        private string _lastDefaultDeviceId;
        private string _lastDeviceSignature;
        private bool _hasLastDefaultSample;
        private bool _deviceWatchStopped;

        public event Action DevicesChanged;

        public AudioService()
        {
            try
            {
                _deviceEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            }
            catch (Exception ex)
            {
                /* intentionally ignored: CoreAudio may be unavailable on headless, VM, or WinPE environments */
                Debug.WriteLine($"[AudioService] Failed to initialize IMMDeviceEnumerator: {ex.Message}");
            }
        }

        /// <summary>
        /// 启用默认端点变化监听。
        ///
        /// 背景：<see cref="RaiseDevicesChanged"/> 此前全仓只有定义、零调用点，
        /// 订阅方（AudioSwitchModule）的订阅/退订全是死代码 —— 插拔耳机后托盘仍显示旧设备名，
        /// 必须重启进程才更新。接口注释承诺的 WM_DEVICECHANGE 桥接一直没实现。
        ///
        /// 实现方式与既有 DisplayPowerListener / Win32ClipboardListener 一致：
        /// 消息专用窗口（HWND_MESSAGE）+ RegisterDeviceNotification，零轮询。
        /// 必须由 UI 线程调用（HwndSource 有线程亲和）。
        /// </summary>
        public void StartDeviceNotifications(Dispatcher dispatcher = null)
        {
            // 由 Host 组合根显式传入 Dispatcher（M33）：不再回退 Application.Current，
            // 避免测试/后台线程拿到属于已结束线程的 Dispatcher。
            var d = dispatcher;
            if (d == null) return;

            // Dispatcher 已关闭时不能 Invoke：消息窗口本就需要活着的消息泵去投递
            // WM_DEVICECHANGE，此时创建窗口毫无意义，只会在别的线程上永久阻塞。
            if (d.HasShutdownStarted || d.HasShutdownFinished)
            {
                Debug.WriteLine("[AudioService] StartDeviceNotifications skipped: dispatcher has shut down");
                return;
            }
            _dispatcher = d;

            void StartCore()
            {
                lock (_deviceWatchLock)
                {
                    if (_deviceWatchStopped || _deviceNotifyWindow != null) return;
                    try
                    {
                        if (_deviceNotifyWindow == null)
                        {
                            var parameters = new HwndSourceParameters("CarroDesk_AudioDeviceWatcher")
                            {
                                Width = 0,
                                Height = 0,
                                WindowStyle = 0,
                                ParentWindow = HWND_MESSAGE
                            };
                            _deviceNotifyWindow = new HwndSource(parameters);
                            _deviceNotifyWindow.AddHook(DeviceWndProc);
                        }

                        IntPtr hwnd = _deviceNotifyWindow.Handle;
                        if (hwnd != IntPtr.Zero)
                        {
                            _deviceNotifyHandle = RegisterDeviceNotification(
                                hwnd,
                                IntPtr.Zero,
                                DEVICE_NOTIFY_WINDOW_HANDLE | DEVICE_NOTIFY_ALL_CLASSES);
                            _lastDefaultDeviceId = GetDefaultPlaybackDevice()?.Id;
                            _lastDeviceSignature = string.Join("|", GetPlaybackDevices()
                                .Select(dev => dev.Id)
                                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
                            _hasLastDefaultSample = true;
                            _deviceNotifyStarted = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        /* intentionally ignored: device notification is a convenience, audio must keep working without it */
                        Debug.WriteLine("[AudioService] StartDeviceNotifications failed: " + ex.Message);
                        _deviceNotifyStarted = false;
                        CleanupWindowLocked(allowUiCleanup: true);
                    }
                }
            }

            if (d.CheckAccess()) StartCore();
            else
            {
                try { d.Invoke(StartCore); } catch { }
            }
        }

        /// <summary>监听是否已成功启动（供自检与测试断言）。</summary>
        public bool IsDeviceNotificationActive
        {
            get { lock (_deviceWatchLock) return _deviceNotifyStarted && _deviceNotifyWindow != null; }
        }

        /// <summary>停止监听（宿主退出 / 服务释放）。</summary>
        public void StopDeviceNotifications()
        {
            Dispatcher d;
            lock (_deviceWatchLock)
            {
                _deviceWatchStopped = true;
                d = _dispatcher;
            }

            void StopUiCore()
            {
                lock (_deviceWatchLock)
                {
                    CleanupWindowLocked(allowUiCleanup: true);
                }
            }

            if (d == null || d.CheckAccess())
            {
                bool onOwnerThread = d == null || d.Thread == System.Threading.Thread.CurrentThread;
                lock (_deviceWatchLock)
                {
                    CleanupWindowLocked(allowUiCleanup: onOwnerThread);
                }
                return;
            }

            if (!d.HasShutdownStarted && !d.HasShutdownFinished)
            {
                try
                {
                    d.Invoke(StopUiCore);
                    return;
                }
                catch { }
            }

            // Dispatcher 已开始/完成关闭或跨线程 Invoke 失败：避免跨线程直接 Dispose HwndSource 抛异常，
            // 仍须安全注销 Win32 设备通知句柄并清空引用。
            lock (_deviceWatchLock)
            {
                CleanupWindowLocked(allowUiCleanup: false);
            }
        }

        private void CleanupWindowLocked(bool allowUiCleanup)
        {
            if (_deviceNotifyHandle != IntPtr.Zero)
            {
                try { UnregisterDeviceNotification(_deviceNotifyHandle); } catch { }
                _deviceNotifyHandle = IntPtr.Zero;
            }
            if (_deviceChangeDebounceTimer != null)
            {
                if (allowUiCleanup)
                {
                    try { _deviceChangeDebounceTimer.Stop(); } catch { }
                }
                _deviceChangeDebounceTimer = null;
            }
            if (_deviceNotifyWindow != null)
            {
                if (allowUiCleanup)
                {
                    try
                    {
                        _deviceNotifyWindow.RemoveHook(DeviceWndProc);
                        _deviceNotifyWindow.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[AudioService] CleanupWindow failed: " + ex.Message);
                    }
                }
                _deviceNotifyWindow = null;
            }
        }

        private IntPtr DeviceWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_DEVICECHANGE || wParam.ToInt32() != DBT_DEVNODES_CHANGED)
            {
                return IntPtr.Zero;
            }

            try
            {
                // 一次插拔会广播一串事件，先合并再通知，避免托盘菜单连续重建
                lock (_deviceWatchLock)
                {
                    if (_deviceChangeDebounceTimer != null) _deviceChangeDebounceTimer.Stop();
                    if (_deviceChangeDebounceTimer == null)
                    {
                        _deviceChangeDebounceTimer = new DispatcherTimer { Interval = DeviceChangeDebounce };
                        _deviceChangeDebounceTimer.Tick += (s, e) =>
                        {
                            ((DispatcherTimer)s).Stop();
                            RaiseDevicesChanged();
                        };
                    }
                    _deviceChangeDebounceTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AudioService] device change handling failed: " + ex.Message);
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// 广播"设备列表/默认端点已变化"。默认端点实际未变时不广播：
        /// DBT_DEVNODES_CHANGED 对所有设备类��生效（键鼠、显示器也会触发），
        /// 无条件广播会让托盘在拔一个 U 盘时也重建一次。
        /// </summary>
        public void RaiseDevicesChanged()
        {
            string currentId = null;
            string currentSig = null;
            try
            {
                currentId = GetDefaultPlaybackDevice()?.Id;
                var devices = GetPlaybackDevices();
                currentSig = string.Join("|", devices
                    .Select(d => d.Id)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AudioService] RaiseDevicesChanged probe failed: " + ex.Message);
            }

            lock (_deviceWatchLock)
            {
                if (_hasLastDefaultSample &&
                    string.Equals(currentId, _lastDefaultDeviceId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(currentSig, _lastDeviceSignature, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                _lastDefaultDeviceId = currentId;
                _lastDeviceSignature = currentSig;
                _hasLastDefaultSample = true;
            }

            DevicesChanged?.Invoke();
        }

        public List<AudioDeviceItem> GetPlaybackDevices()
        {
            var list = new List<AudioDeviceItem>();
            if (_deviceEnumerator == null) return list;

            IMMDeviceCollection collection = null;
            try
            {
                if (_deviceEnumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out collection) == 0 && collection != null)
                {
                    collection.GetCount(out uint count);
                    for (uint i = 0; i < count; i++)
                    {
                        if (collection.Item(i, out var device) == 0 && device != null)
                        {
                            try
                            {
                                device.GetId(out string id);
                                string name = GetDeviceFriendlyName(device);
                                if (!string.IsNullOrEmpty(id))
                                    list.Add(new AudioDeviceItem { Id = id, Name = name ?? id });
                            }
                            finally
                            {
                                if (Marshal.IsComObject(device)) Marshal.ReleaseComObject(device);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioService] GetPlaybackDevices failed: {ex.Message}");
            }
            finally
            {
                if (collection != null && Marshal.IsComObject(collection)) Marshal.ReleaseComObject(collection);
            }

            return list;
        }

        public AudioDeviceItem GetDefaultPlaybackDevice()
        {
            return GetDefaultPlaybackDevice(ERole.eMultimedia);
        }

        private AudioDeviceItem GetDefaultPlaybackDevice(ERole role)
        {
            if (_deviceEnumerator == null) return null;
            try
            {
                if (_deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, role, out var device) == 0 && device != null)
                {
                    try
                    {
                        device.GetId(out string id);
                        string name = GetDeviceFriendlyName(device);
                        return new AudioDeviceItem { Id = id, Name = name ?? id };
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            catch (Exception ex)
            {
                /* intentionally ignored: default audio endpoint may be missing if no audio device is connected */
                Debug.WriteLine($"[AudioService] GetDefaultPlaybackDevice({role}) failed: {ex.Message}");
            }
            return null;
        }

        public bool SetDefaultPlaybackDevice(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;

            try
            {
                var policyConfig = PolicyConfigFactory.CreatePolicyConfig();
                if (policyConfig == null) return false;

                try
                {
                    // 三个默认角色必须保持一致。任一角色失败时，尽力回滚已成功角色，
                    // 禁止 UI 报成功但播放器/通信软件实际使用不同端点。
                    var roles = new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications };
                    string[] previousIds = new string[roles.Length];
                    for (int i = 0; i < roles.Length; i++)
                    {
                        var previous = GetDefaultPlaybackDevice(roles[i]);
                        previousIds[i] = previous?.Id;
                    }

                    int[] results = new int[roles.Length];
                    for (int i = 0; i < roles.Length; i++)
                    {
                        try { results[i] = policyConfig.SetDefaultEndpoint(deviceId, roles[i]); }
                        catch { results[i] = -1; }
                    }

                    bool allSucceeded = true;
                    for (int i = 0; i < results.Length; i++)
                    {
                        if (results[i] == 0) continue;
                        allSucceeded = false;
                    }
                    if (!allSucceeded)
                    {
                        // 任意角色失败都回滚全部角色，不能只恢复“失败角色”而留下已成功的新状态。
                        for (int i = 0; i < roles.Length; i++)
                        {
                            if (string.IsNullOrEmpty(previousIds[i])) continue;
                            try { policyConfig.SetDefaultEndpoint(previousIds[i], roles[i]); } catch { }
                        }
                    }
                    return allSucceeded;
                }
                finally
                {
                    Marshal.ReleaseComObject(policyConfig);
                }
            }
            catch (Exception ex)
            {
                /* intentionally ignored: PolicyConfig COM activation or method call can fail on unsupported Windows versions or non-standard sound devices */
                Debug.WriteLine($"[AudioService] SetDefaultPlaybackDevice failed: {ex.Message}");
                return false;
            }
        }

        public AudioDeviceItem FindDeviceByPattern(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return null;
            var devices = GetPlaybackDevices();
            foreach (var d in devices)
            {
                if (!string.IsNullOrEmpty(d.Name) && d.Name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return d;
                }
            }
            return null;
        }

        public bool SetProcessMute(string processName, bool mute)
        {
            if (string.IsNullOrEmpty(processName) || _deviceEnumerator == null) return false;

            string targetName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName.Substring(0, processName.Length - 4)
                : processName;

            bool anyModified = false;
            ForEachRenderSession("SetProcessMute", (sessionControl, pid, procName) =>
            {
                if (string.Equals(procName, targetName, StringComparison.OrdinalIgnoreCase) &&
                    sessionControl is ISimpleAudioVolume volume)
                {
                    Guid ctx = Guid.Empty;
                    int hr = volume.SetMute(mute, ref ctx);
                    if (hr == 0) anyModified = true;
                    else Debug.WriteLine($"[AudioService] SetMute failed for pid={pid}, hr=0x{hr:X8}");
                }
            });

            return anyModified;
        }

        public void UnmuteProcesses(IEnumerable<string> processNames)
        {
            if (processNames == null) return;
            foreach (var name in processNames)
            {
                try
                {
                    SetProcessMute(name, false);
                }
                catch (Exception ex)
                {
                    /* intentionally ignored: individual process unmute failure should not abort batch */
                    Debug.WriteLine($"[AudioService] UnmuteProcesses failed for '{name}': {ex.Message}");
                }
            }
        }

        private static string GetDeviceFriendlyName(IMMDevice device)
        {
            try
            {
                if (device.OpenPropertyStore(StorageAccessMode.Read, out var store) == 0 && store != null)
                {
                    try
                    {
                        var key = PropertyKey.PKEY_Device_FriendlyName;
                        if (store.GetValue(ref key, out var pv) == 0)
                        {
                            string str = pv.GetString();
                            pv.Clear();
                            return str;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(store);
                    }
                }
            }
            catch (Exception ex)
            {
                /* intentionally ignored: property store read may fail if device was unplugged */
                Debug.WriteLine($"[AudioService] GetDeviceFriendlyName failed: {ex.Message}");
            }
            return null;
        }

        public List<string> GetActiveAudioProcesses()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_deviceEnumerator == null) return new List<string>();

            ForEachRenderSession("GetActiveAudioProcesses", (sessionControl, pid, procName) =>
            {
                result.Add(procName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? procName : procName + ".exe");
            });

            return new List<string>(result);
        }

        private void ForEachRenderSession(string callerName, Action<IAudioSessionControl, uint, string> visitor)
        {
            try
            {
                if (_deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device) != 0 || device == null)
                    return;

                try
                {
                    var iid = typeof(IAudioSessionManager2).GUID;
                    if (device.Activate(ref iid, 0, IntPtr.Zero, out var sessionMgrObj) != 0 || !(sessionMgrObj is IAudioSessionManager2 sessionManager))
                        return;

                    try
                    {
                        if (sessionManager.GetSessionEnumerator(out var sessionEnum) != 0 || sessionEnum == null)
                            return;

                        try
                        {
                            sessionEnum.GetCount(out int count);
                            for (int i = 0; i < count; i++)
                            {
                                if (sessionEnum.GetSession(i, out var sessionControl) != 0 || sessionControl == null)
                                    continue;

                                try
                                {
                                    if (!(sessionControl is IAudioSessionControl2 sessionControl2))
                                        continue;

                                    sessionControl2.GetProcessId(out uint pid);
                                    if (pid == 0) continue;

                                    string procName = null;
                                    try
                                    {
                                        using (var proc = Process.GetProcessById((int)pid))
                                        {
                                            procName = proc.ProcessName;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        /* intentionally ignored: process exited or access denied during iteration */
                                        Debug.WriteLine($"[AudioService] {callerName} GetProcessById({pid}) failed: {ex.Message}");
                                    }

                                    if (!string.IsNullOrEmpty(procName))
                                    {
                                        visitor(sessionControl, pid, procName);
                                    }
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(sessionControl);
                                }
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(sessionEnum);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sessionManager);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            catch (Exception ex)
            {
                /* intentionally ignored: audio session iteration failed (device disconnected or CoreAudio restart) */
                Debug.WriteLine($"[AudioService] {callerName} failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            StopDeviceNotifications();

            if (_deviceEnumerator != null)
            {
                try
                {
                    Marshal.ReleaseComObject(_deviceEnumerator);
                }
                catch (Exception ex)
                {
                    /* intentionally ignored: COM cleanup on dispose */
                    Debug.WriteLine($"[AudioService] Dispose failed releasing enumerator: {ex.Message}");
                }
                _deviceEnumerator = null;
            }
        }
    }
}
