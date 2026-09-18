using CarroDesk.Models;

namespace CarroDesk.Services
{
    /// <summary>
    /// 宿主侧 PIN 服务（规范 §3.5 PIN 能力下沉 Host）。
    /// 惰性从当前配置读取 salt/hash，不依赖 ScreenLock 模块是否存活——
    /// 只要配置里有 PIN，缺模块仍按"有 PIN 则验"兜底。
    /// </summary>
    public class HostPinService : IPinService
    {
        private readonly ConfigService _config;
        private readonly PinService _inner = new PinService();

        private string _pendingSalt;
        private string _pendingHash;

        public HostPinService(ConfigService config)
        {
            _config = config;
        }

        public bool IsConfigured
        {
            get { return !string.IsNullOrEmpty(_pendingSalt) || !string.IsNullOrEmpty(_pendingHash) || Current().HasPin(); }
        }

        public string Salt { get { return _pendingSalt ?? Current().PinSalt; } }
        public string Hash { get { return _pendingHash ?? Current().PinHash; } }

        public bool Verify(string pin)
        {
            BuildFromCurrent();
            bool ok = _inner.Verify(pin);
            if (ok && _inner.JustUpgraded)
            {
                var c = Current();
                if (c != null)
                {
                    c.PinSalt = _inner.Salt;
                    c.PinHash = _inner.Hash;
                    _config?.Save();
                    _inner.ClearUpgraded();
                }
            }
            return ok;
        }

        public void SetNewPin(string pin)
        {
            _inner.SetNewPin(pin);
            _pendingSalt = _inner.Salt;
            _pendingHash = _inner.Hash;
        }

        public void SetFromConfig(string salt, string hash)
        {
            _pendingSalt = null;
            _pendingHash = null;
            _inner.SetFromConfig(salt, hash);
        }

        private AppSettings Current()
        {
            return _config != null ? _config.Current : null;
        }

        private void BuildFromCurrent()
        {
            var c = Current();
            if (c == null) return;
            _inner.SetFromConfig(c.PinSalt, c.PinHash);
        }
    }
}