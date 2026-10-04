using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CarroDesk.Common;
using Newtonsoft.Json;

namespace CarroDesk.Services
{
    public class PinService : IPinService
    {
        private const int Pbkdf2Iterations = 100000;

        public string Salt { get; private set; }
        public string Hash { get; private set; }

        public bool JustUpgraded { get; private set; }

        public void ClearUpgraded()
        {
            JustUpgraded = false;
        }

        public bool IsConfigured
        {
            get { return !string.IsNullOrEmpty(Salt) && !string.IsNullOrEmpty(Hash); }
        }

        public void SetFromConfig(string salt, string hash)
        {
            Salt = salt;
            Hash = hash;
            JustUpgraded = false;
        }

        public void SetNewPin(string pin)
        {
            var salt = GenerateSalt();
            Salt = Convert.ToBase64String(salt);
            Hash = ComputeHash(salt, pin);
            JustUpgraded = false;
        }

        public static byte[] GenerateSalt()
        {
            using (var rng = new RNGCryptoServiceProvider())
            {
                var data = new byte[16];
                rng.GetBytes(data);
                return data;
            }
        }

        public static string ComputeHash(byte[] salt, string pin)
        {
            var password = Encoding.UTF8.GetBytes(Convert.ToBase64String(salt) + ":" + pin);
            using (var derive = new Rfc2898DeriveBytes(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256))
            {
                return "pbkdf2$" + Pbkdf2Iterations + "$" + Convert.ToBase64String(derive.GetBytes(32));
            }
        }

        private static string ComputeLegacyHash(byte[] salt, string pin)
        {
            using (var sha = SHA256.Create())
            {
                var input = Encoding.UTF8.GetBytes(Convert.ToBase64String(salt) + ":" + pin);
                return Convert.ToBase64String(sha.ComputeHash(input));
            }
        }

        public bool Verify(string pin)
        {
            if (!IsConfigured) return false;
            byte[] salt;
            try { salt = Convert.FromBase64String(Salt); }
            catch { return false; }
            var candidate = ComputeHash(salt, pin);
            if (FixedTimeEquals(candidate, Hash)) return true;

            // 兼容旧版单轮 SHA256 哈希，验证通过后透明升级为 PBKDF2
            if (FixedTimeEquals(ComputeLegacyHash(salt, pin), Hash))
            {
                Hash = candidate;
                JustUpgraded = true;
                return true;
            }
            return false;
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    public enum PinAttemptResult
    {
        Success,
        Wrong,
        Blocked
    }

    public class PinGuard
    {
        private const int MaxFreeAttempts = 5;

        /// <summary>
        /// 持久化状态的衰减窗口：距上次记账超过该时长即视为"旧账作废"，
        /// 避免一次手滑留下的计数在几天后仍然把用户挡在门外。
        /// </summary>
        private static readonly TimeSpan StateDecayWindow = TimeSpan.FromHours(24);

        private readonly Func<IPinService> _pinProvider;
        private readonly object _sync = new object();
        private int _fails;
        private DateTime _blockedUntil = DateTime.MinValue;

        /// <summary>
        /// 失败计数/封锁窗口的状态文件路径。为 null（默认）时不落盘，仅内存计数——
        /// 单元测试与不需要跨进程限流的场景走这条路径，避免污染用户数据目录。
        /// 传入路径后，进程重启不再把失败计数清零（否则"输错 4 次 → 重启宿主 → 继续试"可无限试探）。
        /// </summary>
        private readonly string _statePath;

        private DateTime _lastUpdateUtc = DateTime.MinValue;
        private bool _stateLoaded;

        public PinGuard(Func<IPinService> pinProvider, string statePath = null)
        {
            _pinProvider = pinProvider;
            _statePath = statePath;
        }

        public PinGuard(IPinService pin, string statePath = null) : this(() => pin, statePath)
        {
        }

        /// <summary>供宿主注入默认状态文件路径（%AppData%\CarroDesk 或便携目录下的 pin-guard.json）。</summary>
        public static string DefaultStatePath
        {
            get { return Path.Combine(ConfigService.DirPath, "pin-guard.json"); }
        }

        public void Reload(IPinService pin)
        {
            lock (_sync)
            {
                _fails = 0;
                _blockedUntil = DateTime.MinValue;
                _lastUpdateUtc = DateTime.MinValue;
            }
        }

        public TimeSpan RemainingBlock()
        {
            lock (_sync)
            {
                // 读回持久化状态：刚启动的进程也必须看到上一次留下的封锁窗口，
                // 否则界面会显示"无锁定"而实际 Try 直接 Blocked。
                EnsureStateLoadedNoLock();
                return RemainingBlockNoLock();
            }
        }

        private TimeSpan RemainingBlockNoLock()
        {
            var left = _blockedUntil - DateTime.Now;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        /// <summary>
        /// 校验 PIN 并维护失败计数。
        ///
        /// 并发安全说明：调用方位于命名管道的每连接独立 Task 上（最多可并发到
        /// MaxAllowedServerInstances 个），若「判封锁 → 递增计数」不是原子的，
        /// N 个并发请求会同时读到相同的 _fails 并互相覆盖计数，导致 5 次上限失效。
        /// 因此这里采用「锁内预留名额 → 锁外做 PBKDF2 慢运算 → 锁内结算」：
        /// 每次尝试都在锁内原子占用一个名额，锁外的慢验证不阻塞其他请求的计数，
        /// 但也无法跳过计数，从而保证 N 次尝试必然消耗 N 个名额。
        /// </summary>
        public PinAttemptResult Try(string pin, out TimeSpan blockRemaining)
        {
            // 阶段一：锁内原子地检查封锁状态并预留本次尝试的名额。
            lock (_sync)
            {
                EnsureStateLoadedNoLock();
                blockRemaining = RemainingBlockNoLock();
                if (blockRemaining > TimeSpan.Zero) return PinAttemptResult.Blocked;

                _fails++;
                _lastUpdateUtc = DateTime.UtcNow;
                PersistNoLock();
            }

            // 阶段二：锁外执行 PBKDF2 验证（10 万轮，耗时约数十毫秒，不能占用锁）。
            var pinService = _pinProvider?.Invoke();
            var verified = pinService != null && pinService.Verify(pin);

            // 阶段三：锁内按预留名额结算封锁时间。
            lock (_sync)
            {
                if (verified)
                {
                    _fails = 0;
                    _blockedUntil = DateTime.MinValue;
                    _lastUpdateUtc = DateTime.UtcNow;
                    PersistNoLock();
                    blockRemaining = TimeSpan.Zero;
                    return PinAttemptResult.Success;
                }

                if (_fails >= MaxFreeAttempts)
                {
                    _blockedUntil = DateTime.Now.AddSeconds((_fails - MaxFreeAttempts + 1) * 30.0);
                    blockRemaining = RemainingBlockNoLock();
                }
                else
                {
                    blockRemaining = TimeSpan.Zero;
                }
                PersistNoLock();
                return PinAttemptResult.Wrong;
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                EnsureStateLoadedNoLock();
                _fails = 0;
                _blockedUntil = DateTime.MinValue;
                _lastUpdateUtc = DateTime.UtcNow;
                PersistNoLock();
            }
        }

        /// <summary>
        /// 延迟读回持久化状态（首次使用时，且仍在锁内调用）。
        /// 读回只用于恢复失败计数/封锁窗口；超出衰减窗口的旧状态按"过期"丢弃。
        /// </summary>
        private void EnsureStateLoadedNoLock()
        {
            if (_stateLoaded) return;
            _stateLoaded = true;
            if (string.IsNullOrEmpty(_statePath)) return;

            try
            {
                if (!File.Exists(_statePath)) return;
                var json = File.ReadAllText(_statePath, Encoding.UTF8);
                var state = JsonConvert.DeserializeObject<PinGuardState>(json);
                if (state == null) return;
                if (state.Fails <= 0) return;

                // 只接受近期的记账，过期计数自动作废（防止手滑一次被永久连坐）
                if (state.UpdatedUtc == default(DateTime)) return;
                if (DateTime.UtcNow - state.UpdatedUtc > StateDecayWindow) return;

                _fails = state.Fails;
                _blockedUntil = state.BlockedUntilLocal;
                _lastUpdateUtc = state.UpdatedUtc;
            }
            catch
            {
                // 状态文件损坏/不可读：按"无历史失败"继续，绝不让限流自身成为不可用点
            }
        }

        private void PersistNoLock()
        {
            if (string.IsNullOrEmpty(_statePath)) return;
            try
            {
                if (_fails <= 0 && _blockedUntil <= DateTime.MinValue)
                {
                    // 已完全复位：删除状态文件，避免"一次成功校验"永久留下计数痕迹
                    if (File.Exists(_statePath)) File.Delete(_statePath);
                    return;
                }

                var state = new PinGuardState
                {
                    Fails = _fails,
                    BlockedUntilLocal = _blockedUntil,
                    UpdatedUtc = _lastUpdateUtc
                };
                AtomicFile.WriteAllText(_statePath, JsonConvert.SerializeObject(state), Encoding.UTF8);
            }
            catch
            {
                // 落盘失败不影响本次判定：限流降级为进程内生效
            }
        }

        private sealed class PinGuardState
        {
            public int Fails { get; set; }

            /// <summary>封锁截止时刻（本机本地时间，与 <see cref="System.DateTime.Now"/> 同源）。</summary>
            public DateTime BlockedUntilLocal { get; set; }

            /// <summary>最后一次记账的 UTC 时间，用于衰减窗口判定。</summary>
            public DateTime UpdatedUtc { get; set; }
        }
    }
}
