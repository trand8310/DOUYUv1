using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MainClient.UiTask
{
    public sealed class AdTrafficProxyIpStateEntity
    {
        private long _fetched;
        private long _consumed;

        private readonly object _ipsLock = new();
        private List<string> _pendingConsumedIps = new();

        public void AddFetched(long value = 1)
        {
            if (value > 0)
                Interlocked.Add(ref _fetched, value);
        }

        public void AddConsumed(long value = 1)
        {
            if (value > 0)
                Interlocked.Add(ref _consumed, value);
        }

        public void AddConsumedIp(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return;

            lock (_ipsLock)
            {
                _pendingConsumedIps.Add(ip);
            }
        }

        public AdTrafficProxyIpStateSnapshot Snapshot()
        {
            string[] ips;
            lock (_ipsLock)
            {
                ips = _pendingConsumedIps.ToArray();
            }

            return new AdTrafficProxyIpStateSnapshot(
                Fetched: Interlocked.Read(ref _fetched),
                Consumed: Interlocked.Read(ref _consumed),
                ConsumedIps: ips
            );
        }

        public void Commit(AdTrafficProxyIpStateSnapshot snapshot)
        {
            if (snapshot.IsEmpty)
                return;

            if (snapshot.Fetched > 0)
                Interlocked.Add(ref _fetched, -snapshot.Fetched);

            if (snapshot.Consumed > 0)
                Interlocked.Add(ref _consumed, -snapshot.Consumed);

            if (snapshot.ConsumedIps.Length > 0)
            {
                lock (_ipsLock)
                {
                    int removeCount = Math.Min(snapshot.ConsumedIps.Length, _pendingConsumedIps.Count);
                    if (removeCount > 0)
                        _pendingConsumedIps.RemoveRange(0, removeCount);
                }
            }
        }

        public bool IsEmpty()
        {
            lock (_ipsLock)
            {
                return Interlocked.Read(ref _fetched) == 0
                    && Interlocked.Read(ref _consumed) == 0
                    && _pendingConsumedIps.Count == 0;
            }
        }
    }
}
