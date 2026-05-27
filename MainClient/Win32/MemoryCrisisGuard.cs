

namespace MainClient.Win32
{
    using System;
    using System.Threading;

    public static class MemoryCrisisGuard
    {
        private static int _dangerCount;
        private static long _lastDangerTicksUtc;
        private static int _restartIssued;

        public static bool ShouldRestartNow()
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var lastTicks = Interlocked.Read(ref _lastDangerTicksUtc);

            // 超过 60 秒，计数清零
            if (lastTicks != 0 && new TimeSpan(nowTicks - lastTicks) > TimeSpan.FromSeconds(60))
            {
                Interlocked.Exchange(ref _dangerCount, 0);
            }

            Interlocked.Exchange(ref _lastDangerTicksUtc, nowTicks);

            var count = Interlocked.Increment(ref _dangerCount);

            // 连续 3 次才允许
            if (count < 3)
                return false;

            // 保证只触发一次
            return Interlocked.Exchange(ref _restartIssued, 1) == 0;
        }
    }
}
