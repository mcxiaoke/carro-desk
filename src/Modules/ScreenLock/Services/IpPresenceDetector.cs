using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace CarroDesk.Modules.ScreenLock.Services
{
    /// <summary>
    /// IP 设备在场感知探测器（移植自 Carrot.AutoLock）。
    /// 采用 ICMP Ping + ARP 链路层双重探测，用于检测局域网中指定设备（如手机/平板）是否在线。
    /// 纯原生实现，零 WinRT 依赖，零第三方库依赖。
    /// </summary>
    public class IpPresenceDetector
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(uint destIp, uint srcIp, byte[] pMacAddr, ref uint phyAddrLen);

        private readonly object _lock = new object();
        private DateTime? _offlineStartTime;
        private DateTime _lastProbeTime = DateTime.MinValue;
        private bool _lastProbeResult = false;
        private bool _isProbing = false;

        private const int CacheTtlSeconds = 10;
        private const int PingTimeoutMs = 1000;

        /// <summary>
        /// 检查指定 IP 的设备是否判定为在场（包含缓存与离线防抖缓冲判定）。
        /// 若 IP 格式无效返回 false；首次调用同步探测初始化状态；后续调用基于 TTL 缓存并异步刷新。
        /// </summary>
        /// <param name="targetIp">目标设备 IPv4 地址</param>
        /// <param name="graceSeconds">离线缓冲秒数</param>
        /// <returns>true 表示设备在线或仍在离线缓冲期内（应免除锁屏）</returns>
        public bool IsPresentWithGrace(string targetIp, int graceSeconds)
        {
            if (string.IsNullOrWhiteSpace(targetIp)) return false;
            if (!IPAddress.TryParse(targetIp.Trim(), out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return false;

            lock (_lock)
            {
                var now = DateTime.Now;

                // 首次探测：若从未探测过，立即进行同步探测并初始化状态
                if (_lastProbeTime == DateTime.MinValue)
                {
                    bool online = ProbeInternal(address);
                    _lastProbeTime = now;
                    _lastProbeResult = online;
                    if (online)
                    {
                        _offlineStartTime = null;
                        return true;
                    }
                    else
                    {
                        _offlineStartTime = now;
                        return false;
                    }
                }

                // 若在缓存有效期内，直接基于缓存结果与缓冲期综合计算
                if ((now - _lastProbeTime).TotalSeconds < CacheTtlSeconds)
                {
                    return EvaluateGrace(now, graceSeconds);
                }

                // 缓存已过期，若未在探测中则发起异步后台探测
                if (!_isProbing)
                {
                    _isProbing = true;
                    Task.Run(async () =>
                    {
                        bool online = await ProbeAsync(targetIp).ConfigureAwait(false);
                        lock (_lock)
                        {
                            var probeNow = DateTime.Now;
                            _lastProbeTime = probeNow;
                            _lastProbeResult = online;
                            _isProbing = false;

                            if (online)
                            {
                                _offlineStartTime = null;
                            }
                            else if (!_offlineStartTime.HasValue)
                            {
                                _offlineStartTime = probeNow;
                            }
                        }
                    });
                }

                return EvaluateGrace(now, graceSeconds);
            }
        }

        private bool EvaluateGrace(DateTime now, int graceSeconds)
        {
            if (_lastProbeResult) return true;
            if (!_offlineStartTime.HasValue) return false;

            double offlineSecs = (now - _offlineStartTime.Value).TotalSeconds;
            return offlineSecs <= Math.Max(5, graceSeconds);
        }

        /// <summary>
        /// 重置探测器内部状态机（在用户活动、解锁或配置重载时调用）。
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _offlineStartTime = null;
                _lastProbeTime = DateTime.MinValue;
                _lastProbeResult = false;
                _isProbing = false;
            }
        }

        /// <summary>
        /// 单次即时探测指定 IP 是否在线（供 UI 测试按钮或主动探测使用）。
        /// 双重探测：优先 Ping，失败则降级为 SendARP。
        /// </summary>
        public static async Task<bool> ProbeAsync(string targetIp)
        {
            if (string.IsNullOrWhiteSpace(targetIp)) return false;
            if (!IPAddress.TryParse(targetIp.Trim(), out var address)) return false;
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

            // 第一层：Ping（针对活跃亮屏设备，速度最快）
            try
            {
                using (var ping = new Ping())
                {
                    var reply = await ping.SendPingAsync(address, PingTimeoutMs).ConfigureAwait(false);
                    if (reply != null && reply.Status == IPStatus.Success)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Ping 异常不致命，直接降级到 ARP
            }

            // 第二层：ARP 探测（针对息屏休眠忽略 ICMP 的移动设备）
            return ProbeArp(address);
        }

        private static bool ProbeInternal(IPAddress address)
        {
            if (ProbeArp(address)) return true;

            try
            {
                using (var ping = new Ping())
                {
                    var reply = ping.Send(address, 500);
                    return reply != null && reply.Status == IPStatus.Success;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool ProbeArp(IPAddress ipAddress)
        {
            try
            {
                byte[] ipBytes = ipAddress.GetAddressBytes();
                if (ipBytes.Length != 4) return false;

                uint destIp = BitConverter.ToUInt32(ipBytes, 0);
                byte[] macAddr = new byte[6];
                uint macAddrLen = (uint)macAddr.Length;

                int ret = SendARP(destIp, 0, macAddr, ref macAddrLen);
                return ret == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
