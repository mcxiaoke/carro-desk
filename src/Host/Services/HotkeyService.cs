using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using CarroDesk.Core;
using CarroDesk.Models;
using CarroDesk.Services.Localization;

namespace CarroDesk.Services.Tasks
{
    internal class HotkeyService : IDisposable, IHotkeyService
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        private static HotkeyService _instance;
        private static readonly object _lock = new object();
        private int _nextId = 0x1000;
        private readonly Dictionary<int, Action> _map = new Dictionary<int, Action>();
        private readonly Dictionary<int, string> _owner = new Dictionary<int, string>();
        private readonly Dictionary<uint, int> _chordToId = new Dictionary<uint, int>();
        private HwndSource _hwndSource;
        private bool _created;

        public static HotkeyService Instance
        {
            get
            {
                lock (_lock)
                {
                    if (_instance == null) _instance = new HotkeyService();
                    return _instance;
                }
            }
        }

        private HotkeyService()
        {
            try
            {
                var parameters = new HwndSourceParameters("CarroDesk_HotkeyService")
                {
                    ParentWindow = HWND_MESSAGE
                };
                _hwndSource = new HwndSource(parameters);
                _hwndSource.AddHook(WndProc);
                _created = true;
            }
            catch
            {
                _created = false;
            }
        }

        public int Register(string hotkey, Action callback, out string error)
        {
            return RegisterCore("unknown", hotkey, callback, out error);
        }

        public int Register(string moduleId, string hotkey, Action callback, out string error)
        {
            return RegisterCore(moduleId, hotkey, callback, out error);
        }

        private int RegisterCore(string moduleId, string hotkey, Action callback, out string error)
        {
            error = null;
            if (!_created || _hwndSource == null || _hwndSource.Handle == IntPtr.Zero)
            {
                error = "hotkey window not created";
                return 0;
            }

            int mods, vk;
            string err;
            if (!HotkeyHelper.TryParse(hotkey, out mods, out vk, out err))
            {
                error = err;
                return 0;
            }

            uint chord = ((uint)mods << 16) | (uint)vk;

            lock (_lock)
            {
                // 内部冲突检测
                if (_chordToId.TryGetValue(chord, out int existingId) && _owner.TryGetValue(existingId, out string existingModule))
                {
                    error = Loc.T("HotkeyErr.ConflictInternal", "快捷键 '{0}' 与内部模块 [{1}] 冲突", hotkey, existingModule);
                    return 0;
                }

                int id = ++_nextId;
                bool ok = RegisterHotKey(_hwndSource.Handle, id, mods, vk);
                if (!ok)
                {
                    int e = Marshal.GetLastWin32Error();
                    error = e == 1409
                        ? Loc.T("HotkeyErr.OccupiedExternal", "快捷键 '{0}' 已被系统或其他外部程序占用 (Win32: 1409)", hotkey)
                        : $"RegisterHotKey failed code={e}";
                    return 0;
                }

                _map[id] = callback;
                _owner[id] = moduleId ?? "unknown";
                _chordToId[chord] = id;
                return id;
            }
        }

        public void Unregister(int id)
        {
            UnregisterCore(id);
        }

        public void Unregister(string moduleId, int id)
        {
            UnregisterCore(id);
        }

        private void UnregisterCore(int id)
        {
            if (id == 0) return;
            lock (_lock)
            {
                if (_hwndSource != null && _hwndSource.Handle != IntPtr.Zero)
                {
                    try { UnregisterHotKey(_hwndSource.Handle, id); } catch { }
                }

                _map.Remove(id);
                _owner.Remove(id);

                uint keyToRemove = 0;
                bool found = false;
                foreach (var kv in _chordToId)
                {
                    if (kv.Value == id)
                    {
                        keyToRemove = kv.Key;
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    _chordToId.Remove(keyToRemove);
                }
            }
        }

        public void UnregisterAll(string moduleId)
        {
            if (string.IsNullOrEmpty(moduleId)) return;
            List<int> ids;
            lock (_lock)
            {
                ids = new List<int>();
                foreach (var kv in _owner)
                {
                    if (string.Equals(kv.Value, moduleId, StringComparison.OrdinalIgnoreCase))
                        ids.Add(kv.Key);
                }
            }
            foreach (var id in ids)
            {
                UnregisterCore(id);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                Action cb = null;
                lock (_lock) _map.TryGetValue(id, out cb);
                if (cb != null)
                {
                    try { cb(); } catch { }
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_hwndSource != null)
                {
                    foreach (var id in new List<int>(_map.Keys))
                    {
                        try { UnregisterHotKey(_hwndSource.Handle, id); } catch { }
                    }
                    _map.Clear();
                    _owner.Clear();
                    _chordToId.Clear();

                    try
                    {
                        _hwndSource.RemoveHook(WndProc);
                        _hwndSource.Dispose();
                    }
                    catch { }
                    _hwndSource = null;
                    _created = false;
                }
            }
        }
    }
}
