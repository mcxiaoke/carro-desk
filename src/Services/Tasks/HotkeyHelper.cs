using System;

namespace CarroDesk.Services.Tasks
{
    public static class HotkeyHelper
    {
        public static bool Validate(string hotkey, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(hotkey)) { error = "empty"; return false; }
            int mods;
            int vk;
            if (!TryParse(hotkey, out mods, out vk, out error)) return false;
            if (vk == 0) { error = "no key"; return false; }
            return true;
        }

        public static bool TryParse(string hotkey, out int mods, out int vk, out string error)
        {
            mods = 0; vk = 0; error = null;
            if (string.IsNullOrWhiteSpace(hotkey)) { error = "empty"; return false; }
            // format: Ctrl+Alt+Shift+Win+Key  (case insensitive, + or - separator)
            string s = hotkey.Trim();
            s = s.Replace("-", "+");
            var parts = s.Split(new char[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { error = "empty"; return false; }
            string keyPart = parts[parts.Length - 1].Trim();
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string m = parts[i].Trim().ToLowerInvariant();
                if (m == "ctrl" || m == "control") mods |= 0x0002;
                else if (m == "alt") mods |= 0x0001;
                else if (m == "shift") mods |= 0x0004;
                else if (m == "win" || m == "windows" || m == "meta") mods |= 0x0008;
                else { error = "unknown modifier " + parts[i]; return false; }
            }
            // key: single char A-Z, 0-9, F1-24, or names like Space, Enter, Esc
            string k = keyPart.ToUpperInvariant();
            if (k.Length == 1)
            {
                char c = k[0];
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    vk = (int)c;
                    return true;
                }
                switch (c)
                {
                    case '`': case '~': vk = 0xC0; return true; // VK_OEM_3
                    case '-': case '_': vk = 0xBD; return true; // VK_OEM_MINUS
                    case '=': case '+': vk = 0xBB; return true; // VK_OEM_PLUS
                    case '[': case '{': vk = 0xDB; return true; // VK_OEM_4
                    case ']': case '}': vk = 0xDD; return true; // VK_OEM_6
                    case '\\': case '|': vk = 0xDC; return true; // VK_OEM_5
                    case ';': case ':': vk = 0xBA; return true; // VK_OEM_1
                    case '\'': case '"': vk = 0xDE; return true; // VK_OEM_7
                    case ',': case '<': vk = 0xBC; return true; // VK_OEM_COMMA
                    case '.': case '>': vk = 0xBE; return true; // VK_OEM_PERIOD
                    case '/': case '?': vk = 0xBF; return true; // VK_OEM_2
                }
            }
            // try function keys
            if (k.StartsWith("F"))
            {
                int fn;
                if (int.TryParse(k.Substring(1), out fn) && fn >= 1 && fn <= 24)
                {
                    vk = 0x70 + (fn - 1); // VK_F1=0x70
                    return true;
                }
            }
            // named keys
            switch (k)
            {
                case "`": case "~": case "GRAVE": case "BACKQUOTE": case "TILDE": case "OEM3": vk = 0xC0; return true;
                case "MINUS": case "DASH": vk = 0xBD; return true;
                case "PLUS": case "EQUAL": case "EQUALS": vk = 0xBB; return true;
                case "SPACE": vk = 0x20; return true;
                case "ENTER": case "RETURN": vk = 0x0D; return true;
                case "ESC": case "ESCAPE": vk = 0x1B; return true;
                case "TAB": vk = 0x09; return true;
                case "BACKSPACE": case "BACK": vk = 0x08; return true;
                case "INS": case "INSERT": vk = 0x2D; return true;
                case "DEL": case "DELETE": vk = 0x2E; return true;
                case "HOME": vk = 0x24; return true;
                case "END": vk = 0x23; return true;
                case "PGUP": case "PAGEUP": vk = 0x21; return true;
                case "PGDN": case "PAGEDOWN": vk = 0x22; return true;
                case "LEFT": vk = 0x25; return true;
                case "UP": vk = 0x26; return true;
                case "RIGHT": vk = 0x27; return true;
                case "DOWN": vk = 0x28; return true;
                default: error = "unknown key " + keyPart; return false;
            }
        }
    }
}
