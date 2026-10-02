using System;
using System.Security.Cryptography;
using System.Text;

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

        private readonly Func<IPinService> _pinProvider;
        private readonly object _sync = new object();
        private int _fails;
        private DateTime _blockedUntil = DateTime.MinValue;

        public PinGuard(Func<IPinService> pinProvider)
        {
            _pinProvider = pinProvider;
        }

        public PinGuard(IPinService pin) : this(() => pin)
        {
        }

        public void Reload(IPinService pin)
        {
            lock (_sync)
            {
                _fails = 0;
                _blockedUntil = DateTime.MinValue;
            }
        }

        public TimeSpan RemainingBlock()
        {
            lock (_sync)
            {
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
                blockRemaining = RemainingBlockNoLock();
                if (blockRemaining > TimeSpan.Zero) return PinAttemptResult.Blocked;

                _fails++;
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
                return PinAttemptResult.Wrong;
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _fails = 0;
                _blockedUntil = DateTime.MinValue;
            }
        }
    }
}
