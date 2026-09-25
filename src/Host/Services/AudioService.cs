using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using CarroDesk.Core;
using CarroDesk.Core.Audio;
using CarroDesk.Core.Models;

namespace CarroDesk.Host.Services
{
    public class AudioService : IAudioService
    {
        private IMMDeviceEnumerator _deviceEnumerator;

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

        public void RaiseDevicesChanged()
        {
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

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
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

            try
            {
                if (_deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device) == 0 && device != null)
                {
                    try
                    {
                        var iid = typeof(IAudioSessionManager2).GUID;
                        if (device.Activate(ref iid, 0, IntPtr.Zero, out var sessionMgrObj) == 0 && sessionMgrObj is IAudioSessionManager2 sessionManager)
                        {
                            try
                            {
                                if (sessionManager.GetSessionEnumerator(out var sessionEnum) == 0 && sessionEnum != null)
                                {
                                    try
                                    {
                                        sessionEnum.GetCount(out int count);
                                        for (int i = 0; i < count; i++)
                                        {
                                            if (sessionEnum.GetSession(i, out var sessionControl) == 0 && sessionControl != null)
                                            {
                                                try
                                                {
                                                    if (sessionControl is IAudioSessionControl2 sessionControl2)
                                                    {
                                                        sessionControl2.GetProcessId(out uint pid);
                                                        if (pid > 0)
                                                        {
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
                                                                /* intentionally ignored: process may exit or access denied during iteration */
                                                                Debug.WriteLine($"[AudioService] GetProcessById({pid}) failed: {ex.Message}");
                                                            }

                                                            if (!string.IsNullOrEmpty(procName) && string.Equals(procName, targetName, StringComparison.OrdinalIgnoreCase))
                                                            {
                                                                if (sessionControl is ISimpleAudioVolume volume)
                                                                {
                                                                    Guid ctx = Guid.Empty;
                                                                    int hr = volume.SetMute(mute, ref ctx);
                                                                    if (hr == 0) anyModified = true;
                                                                    else Debug.WriteLine($"[AudioService] SetMute failed for pid={pid}, hr=0x{hr:X8}");
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                                finally
                                                {
                                                    Marshal.ReleaseComObject(sessionControl);
                                                }
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        Marshal.ReleaseComObject(sessionEnum);
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(sessionManager);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            catch (Exception ex)
            {
                /* intentionally ignored: audio session enumeration failed (device removed or CoreAudio restart) */
                Debug.WriteLine($"[AudioService] SetProcessMute failed: {ex.Message}");
            }

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

            try
            {
                if (_deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device) == 0 && device != null)
                {
                    try
                    {
                        var iid = typeof(IAudioSessionManager2).GUID;
                        if (device.Activate(ref iid, 0, IntPtr.Zero, out var sessionMgrObj) == 0 && sessionMgrObj is IAudioSessionManager2 sessionManager)
                        {
                            try
                            {
                                if (sessionManager.GetSessionEnumerator(out var sessionEnum) == 0 && sessionEnum != null)
                                {
                                    try
                                    {
                                        sessionEnum.GetCount(out int count);
                                        for (int i = 0; i < count; i++)
                                        {
                                            if (sessionEnum.GetSession(i, out var sessionControl) == 0 && sessionControl != null)
                                            {
                                                try
                                                {
                                                    if (sessionControl is IAudioSessionControl2 sessionControl2)
                                                    {
                                                        sessionControl2.GetProcessId(out uint pid);
                                                        if (pid > 0)
                                                        {
                                                            try
                                                            {
                                                                using (var proc = Process.GetProcessById((int)pid))
                                                                {
                                                                    string pName = proc.ProcessName;
                                                                    if (!string.IsNullOrEmpty(pName))
                                                                    {
                                                                        result.Add(pName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pName : pName + ".exe");
                                                                    }
                                                                }
                                                            }
                                                            catch (Exception ex)
                                                            {
                                                                /* intentionally ignored: process exited or access denied during active process iteration */
                                                                Debug.WriteLine($"[AudioService] Active session GetProcessById({pid}) failed: {ex.Message}");
                                                            }
                                                        }
                                                    }
                                                }
                                                finally
                                                {
                                                    Marshal.ReleaseComObject(sessionControl);
                                                }
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        Marshal.ReleaseComObject(sessionEnum);
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(sessionManager);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            catch (Exception ex)
            {
                /* intentionally ignored: audio session iteration failed (device disconnected) */
                Debug.WriteLine($"[AudioService] GetActiveAudioProcesses failed: {ex.Message}");
            }

            return new List<string>(result);
        }

        public void Dispose()
        {
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
