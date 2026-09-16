using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CarroDesk.Core;
using ScreenLock.Models;

namespace ScreenLock.Services.Tasks
{
    internal class HotkeyService : NativeWindow, IDisposable, IHotkeyService
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;

        private static HotkeyService _instance;
        private static readonly object _lock = new object();
        private int _nextId = 0x1000;
        private Dictionary<int, Action> _map = new Dictionary<int, Action>();
        private Dictionary<int, string> _owner = new Dictionary<int, string>();
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
                CreateParams cp = new CreateParams();
                cp.Caption = "ScreenLock_Hotkey";
                this.CreateHandle(cp);
                _created = true;
            }
            catch { }
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
            if (!_created) { error = "hotkey window not created"; return 0; }
            int mods, vk;
            string err;
            if (!HotkeyHelper.TryParse(hotkey, out mods, out vk, out err))
            {
                error = err;
                return 0;
            }
            int id = ++_nextId;
            bool ok = RegisterHotKey(this.Handle, id, mods, vk);
            if (!ok)
            {
                int e = Marshal.GetLastWin32Error();
                error = "RegisterHotKey failed code=" + e + " (maybe in use)";
                return 0;
            }
            lock (_lock)
            {
                _map[id] = callback;
                _owner[id] = moduleId ?? "unknown";
            }
            return id;
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
            try { UnregisterHotKey(this.Handle, id); } catch { }
            lock (_lock)
            {
                _map.Remove(id);
                _owner.Remove(id);
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

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                Action cb = null;
                lock (_lock) _map.TryGetValue(id, out cb);
                if (cb != null)
                {
                    try { cb(); } catch { }
                }
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            try
            {
                lock (_lock)
                {
                    foreach (var id in new List<int>(_map.Keys))
                    {
                        try { UnregisterHotKey(this.Handle, id); } catch { }
                    }
                    _map.Clear();
                }
                if (_created) this.DestroyHandle();
            }
            catch { }
        }
    }
}
