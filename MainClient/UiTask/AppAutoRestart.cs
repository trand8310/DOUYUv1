using System.Diagnostics;

namespace MainClient.UiTask
{
    using System.Diagnostics;
    using System.Threading;

    public sealed class AppAutoRestart : IDisposable
    {
        private static readonly TimeSpan RestartCooldown = TimeSpan.FromMinutes(2);

        private static int _restartRequested;
        private static long _lastRestartRequestUtcTicks;

        private readonly Func<bool> _shouldRestart;
        private readonly TimeSpan _interval;
        private readonly object _sync = new();

        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private int _started;
        private int _disposed;

        public AppAutoRestart(TimeSpan interval, Func<bool> shouldRestart)
        {
            if (interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interval));

            _interval = interval;
            _shouldRestart = shouldRestart ?? throw new ArgumentNullException(nameof(shouldRestart));
        }

        public void Start()
        {
            ThrowIfDisposed();

            lock (_sync)
            {
                StopInternal(waitLoop: true);

                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                _started = 1;

                _loopTask = Task.Run(async () =>
                {
                    try
                    {
                        using var timer = new PeriodicTimer(_interval);

                        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                        {
                            bool shouldRestart = false;

                            try
                            {
                                shouldRestart = _shouldRestart();
                            }
                            catch
                            {
                                // 条件检查异常时，不触发重启，继续下一轮
                                continue;
                            }

                            if (!shouldRestart)
                                continue;

                            TryRestartApplication();
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _started, 0);
                    }
                }, token);
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                StopInternal(waitLoop: false);
            }
        }

        private void StopInternal(bool waitLoop)
        {
            var cts = _cts;
            var loopTask = _loopTask;

            _cts = null;
            _loopTask = null;

            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch
                {
                }
            }

            if (waitLoop && loopTask != null)
            {
                try
                {
                    loopTask.GetAwaiter().GetResult();
                }
                catch
                {
                }
            }

            if (cts != null)
            {
                try
                {
                    cts.Dispose();
                }
                catch
                {
                }
            }

            Interlocked.Exchange(ref _started, 0);
        }

        private static void TryRestartApplication()
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var lastTicks = Interlocked.Read(ref _lastRestartRequestUtcTicks);

            if (lastTicks > 0)
            {
                var elapsed = TimeSpan.FromTicks(nowTicks - lastTicks);
                if (elapsed >= TimeSpan.Zero && elapsed < RestartCooldown)
                    return;
            }

            if (Interlocked.CompareExchange(ref _restartRequested, 1, 0) != 0)
                return;

            try
            {
                lastTicks = Interlocked.Read(ref _lastRestartRequestUtcTicks);
                if (lastTicks > 0)
                {
                    var elapsed = TimeSpan.FromTicks(nowTicks - lastTicks);
                    if (elapsed >= TimeSpan.Zero && elapsed < RestartCooldown)
                        return;
                }

                Interlocked.Exchange(ref _lastRestartRequestUtcTicks, nowTicks);

                var exePath = Application.ExecutablePath;
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "restart",
                    WorkingDirectory = Path.GetDirectoryName(exePath)!,
                    UseShellExecute = true
                });

                // 只有成功拉起新进程才退出当前进程
                if (process != null)
                {
                    Environment.Exit(1);
                }
            }
            catch
            {
                // 启动失败，允许后续重试
            }
            finally
            {
                // 注意：只有没有退出当前进程时，才会走到这里
                Interlocked.Exchange(ref _restartRequested, 0);
            }
        }

        public bool IsRunning => Volatile.Read(ref _started) == 1;

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(AppAutoRestart));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            lock (_sync)
            {
                StopInternal(waitLoop: true);
            }
        }
    }
}
