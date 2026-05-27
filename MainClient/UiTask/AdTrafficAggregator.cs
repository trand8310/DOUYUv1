using MainClient.Extensions;
using MainClient.Infrastructure;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading.Channels;



namespace MainClient.UiTask
{
    #region Models

    public enum AdTrafficProxyIpKind
    {
        Fetched,
        Consumed
    }

    public record AdTrafficTaskStateEvent(
        int TaskId,
        AdTrafficTaskStateKind State,
        int Count,
        string? Data = null);

    public record AdTrafficTaskProxyIpStateEvent(
        int TaskId,
        AdTrafficProxyIpKind Kind,
        string? Ip = null,
        int Count = 1
    );

    public readonly record struct AdTrafficTaskStateSnapshot(long Start, long Dsp, long Click)
    {
        public bool IsEmpty => Start == 0 && Dsp == 0 && Click == 0;
    }

    public readonly record struct AdTrafficProxyIpStateSnapshot(long Fetched, long Consumed, string[] ConsumedIps)
    {
        public bool IsEmpty => Fetched == 0 && Consumed == 0 && (ConsumedIps == null || ConsumedIps.Length == 0);
    }

    #endregion

    public sealed class AdTrafficLocalHourTaskState
    {
        public string HourKey { get; }

        public ConcurrentDictionary<int, ConcurrentDictionary<string, long>> BufferData { get; }

        public AdTrafficLocalHourTaskState(string hourKey)
        {
            HourKey = hourKey;
            BufferData = new ConcurrentDictionary<int, ConcurrentDictionary<string, long>>(
                Environment.ProcessorCount, 32);
        }
    }

    public sealed class AdTrafficAggregator : IAsyncDisposable, IDisposable
    {
        #region Fields

        /// <summary>
        /// 任务队列
        /// </summary>
        private readonly Channel<AdTrafficTaskStateEvent> _taskStateQueue;
        /// <summary>
        /// Ip队列
        /// </summary>
        private readonly Channel<AdTrafficTaskProxyIpStateEvent> _proxyIpStateQueue;

        private readonly ConcurrentDictionary<int, AdTrafficTaskStateEntity> _taskStates = new();
        private readonly ConcurrentDictionary<int, AdTrafficProxyIpStateEntity> _proxyIpStates = new();
        private readonly AdTrafficTaskStateEntity _hostTaskStates = new();
        public readonly record struct TaskHourKey(int TaskId, string HourKey);
        private static TaskHourKey GetClickHourKey(int taskId)
        {
            return new TaskHourKey(taskId, GetHourKey());
        }

        private readonly ConcurrentDictionary<TaskHourKey, AdTrafficTaskStateEntity> _taskGlobalBaseline = new();
        private readonly ConcurrentDictionary<TaskHourKey, double> _taskClickRates = new();
        private readonly ConcurrentDictionary<TaskHourKey, SemaphoreSlim> _baselineInitLocks = new();

        private readonly AdeHelper _adeHelper;
        private readonly AppSettings _appSettings;
        private readonly ILogger _logger;
        private CancellationTokenSource? _runCts;
        private readonly SemaphoreSlim _flushSemaphore;
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private readonly int _retryCount;
        private readonly int _maxConcurrentRequests;
        private Task? _processTaskQueue;
        private Task? _processIpQueue;
        private Task? _flushLoopTask;
        // 0 = new, 1 = running, 2 = stopping, 3 = stopped, 4 = disposed
        private int _state;

        #endregion

        public AdTrafficAggregator(
            AdeHelper adeHelper,
            AppSettings appSettings,
            ILogger<AdTrafficAggregator> logger,
            int maxConcurrentRequests = 5,
            int retryCount = 3)
        {
            _adeHelper = adeHelper;
            _appSettings = appSettings;
            _logger = logger;

            _retryCount = retryCount < 0 ? 0 : retryCount;
            _maxConcurrentRequests = maxConcurrentRequests <= 0 ? 5 : maxConcurrentRequests;
            _flushSemaphore = new SemaphoreSlim(_maxConcurrentRequests);


            _taskStateQueue = Channel.CreateUnbounded<AdTrafficTaskStateEvent>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });

            _proxyIpStateQueue = Channel.CreateUnbounded<AdTrafficTaskProxyIpStateEvent>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });

            _state = 0;
        }

        #region Lifecycle

        public bool IsStarted => Volatile.Read(ref _state) == 1;
        public bool IsStopping => Volatile.Read(ref _state) == 2;
        public bool IsStopped => Volatile.Read(ref _state) == 3;
        public bool IsDisposed => Volatile.Read(ref _state) == 4;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var state = Volatile.Read(ref _state);

                if (state == 1)
                    return;

                if (state == 2)
                    throw new InvalidOperationException("AdTrafficAggregator is stopping and cannot be started.");

                if (state == 4)
                    throw new ObjectDisposedException(nameof(AdTrafficAggregator));

                _runCts = new CancellationTokenSource();

                _processTaskQueue = Task.Run(() => ProcessTaskStateQueueAsync(_runCts.Token));
                _processIpQueue = Task.Run(() => ProcessProxyIpQueueAsync(_runCts.Token));
                _flushLoopTask = Task.Run(() => FlushLoopAsync(_runCts.Token));


                Volatile.Write(ref _state, 1);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var state = Volatile.Read(ref _state);

                if (state == 0 || state == 3)
                {
                    Volatile.Write(ref _state, 3);
                    return;
                }

                if (state == 4)
                    return;

                if (state == 2)
                    return;

                Volatile.Write(ref _state, 2);

                _taskStateQueue.Writer.TryComplete();
                _proxyIpStateQueue.Writer.TryComplete();
            }
            finally
            {
                _lifecycleLock.Release();
            }

            // 先等消费线程尽量把 channel 内数据吃完
            await WaitConsumersDrainAsync().ConfigureAwait(false);

            // 再做一次停机前强制 flush
            await FlushOnceAsync(cancellationToken).ConfigureAwait(false);

            // 最后停掉周期 flush
            var cts = _runCts;
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

            await WaitBackgroundTasksAsync().ConfigureAwait(false);

            cts?.Dispose();
            _runCts = null;

            Volatile.Write(ref _state, 3);
        }

        #endregion

        #region Public API

        public void EnqueueTaskState(AdTrafficTaskStateEvent ev)
        {
            if (!IsStarted)
                return;

            _taskStateQueue.Writer.TryWrite(ev);
        }

        public void EnqueueFetchedIp(int taskId, int count = 1)
        {
            if (!IsStarted)
                return;

            _proxyIpStateQueue.Writer.TryWrite(new AdTrafficTaskProxyIpStateEvent(taskId, AdTrafficProxyIpKind.Fetched, null, count));
        }

        public void EnqueueConsumedIp(int taskId, string ip, int count = 1)
        {
            if (!IsStarted)
                return;

            _proxyIpStateQueue.Writer.TryWrite(new AdTrafficTaskProxyIpStateEvent(taskId, AdTrafficProxyIpKind.Consumed, ip, count));
        }

        /// <summary>
        /// 获取指定任务的执行状态
        /// </summary>
        /// <param name="taskId"></param>
        /// <returns></returns>
        public AdTrafficTaskStateEntity? GetTaskStateById(int taskId) => _taskStates.TryGetValue(taskId, out var stats) ? stats : null;

        /// <summary>
        /// 获取主机的执行状态
        /// </summary>
        /// <returns></returns>
        public AdTrafficTaskStateEntity GetHostTaskStats() => _hostTaskStates;

        public async Task<double> GetClickRatioAsync(int taskId, double taskCtr = 100)
        {
            await EnsureTaskBaselineAsync(taskId, taskCtr).ConfigureAwait(false);

            var key = GetClickHourKey(taskId);
            var baseline = _taskGlobalBaseline[key];
            var stats = _taskStates.GetOrAdd(taskId, _ => new AdTrafficTaskStateEntity());

            long totalDsp = baseline.DSP + stats.DSP;
            if (totalDsp <= 0)
                return 0;

            long totalClick = baseline.Clickthrough + stats.Clickthrough;
            return totalClick / (double)totalDsp;
        }

        public async Task<bool> CanClickthroughAsync(int taskId, double taskCtr = 100)
        {
            await EnsureTaskBaselineAsync(taskId, taskCtr).ConfigureAwait(false);

            var key = GetClickHourKey(taskId);
            var baseline = _taskGlobalBaseline[key];
            var stats = _taskStates.GetOrAdd(taskId, _ => new AdTrafficTaskStateEntity());
            var rate = _taskClickRates.TryGetValue(key, out var r) ? r : taskCtr;

            if (rate <= 0)
                return false;

            long totalDsp = baseline.DSP + stats.DSP;
            if (totalDsp <= 0)
                return true;

            long totalClick = baseline.Clickthrough + stats.Clickthrough;
            long targetClick = (long)Math.Floor(totalDsp * rate * 0.01);

            return totalClick < targetClick;
        }


        #endregion

        #region Queue Processing

        private async Task ProcessTaskStateQueueAsync(CancellationToken token)
        {
            var buffer = new List<AdTrafficTaskStateEvent>(256);

            try
            {
                while (await _taskStateQueue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (_taskStateQueue.Reader.TryRead(out var ev))
                    {
                        buffer.Add(ev);

                        if (buffer.Count >= 256)
                            break;
                    }

                    foreach (var item in buffer)
                    {
                        var state = _taskStates.GetOrAdd(item.TaskId, _ => new AdTrafficTaskStateEntity());

                        if (item.State == AdTrafficTaskStateKind.X5Sec)
                        {
                           // _logger.LogX5Sec($"x5sec ip={item.Data}");
                            continue;
                        }

                        state.Add(item.State, item.Count);
                        _hostTaskStates.Add(item.State, item.Count);
                    }

                    buffer.Clear();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProcessTaskStateQueueAsync crashed.");
            }
        }

        private async Task ProcessProxyIpQueueAsync(CancellationToken token)
        {
            try
            {
                while (await _proxyIpStateQueue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (_proxyIpStateQueue.Reader.TryRead(out var ev))
                    {
                        var stat = _proxyIpStates.GetOrAdd(ev.TaskId, _ => new AdTrafficProxyIpStateEntity());

                        if (ev.Kind == AdTrafficProxyIpKind.Fetched)
                        {
                            stat.AddFetched(ev.Count);
                        }
                        else
                        {
                            stat.AddConsumed(ev.Count);
                            if (!string.IsNullOrWhiteSpace(ev.Ip))
                                stat.AddConsumedIp(ev.Ip!);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProcessProxyIpQueueAsync crashed.");
            }
        }


        #endregion

        #region Flush

        private async Task FlushLoopAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    await FlushOnceAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FlushLoopAsync crashed.");
            }
        }

        /// <summary>
        /// 主动执行一次 flush。
        /// 停机前会调用这个方法，把当前内存里的统计尽量再推一次。
        /// </summary>
        public async Task FlushOnceAsync(CancellationToken token = default)
        {
            var flushTasks = new List<Task>(64);

            foreach (var pair in _taskStates)
            {
                int taskId = pair.Key;
                AdTrafficTaskStateEntity state = pair.Value;

                var snapshot = state.GetSnapshot();
                if (snapshot.IsEmpty)
                    continue;

                var metrics = state.ToMetricDictionary(snapshot);
                if (metrics.Count == 0)
                    continue;

                flushTasks.Add(FlushTaskStateAsync(taskId, state, snapshot, metrics, token));
            }

            foreach (var pair in _proxyIpStates)
            {
                int taskId = pair.Key;
                AdTrafficProxyIpStateEntity stat = pair.Value;

                var snapshot = stat.Snapshot();
                if (snapshot.IsEmpty)
                    continue;

                var metrics = new Dictionary<string, long>(2);
                if (snapshot.Fetched > 0) metrics["fetched"] = snapshot.Fetched;
                if (snapshot.Consumed > 0) metrics["consumed"] = snapshot.Consumed;

                flushTasks.Add(FlushProxyIpStateAsync(taskId, stat, snapshot, metrics, token));
            }

            {
                var delta = _hostTaskStates.GetSnapshot();
                if (!delta.IsEmpty)
                {
                    var metrics = _hostTaskStates.ToMetricDictionary(delta);
                    if (metrics.Count > 0)
                        flushTasks.Add(FlushHostStateAsync(delta, metrics, token));
                }
            }

            if (flushTasks.Count > 0)
                await Task.WhenAll(flushTasks).ConfigureAwait(false);
        }

        private async Task FlushTaskStateAsync(
            int taskId,
            AdTrafficTaskStateEntity stats,
            AdTrafficTaskStateSnapshot snapshot,
            Dictionary<string, long> metrics,
            CancellationToken token)
        {
            await _flushSemaphore.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await RetryAsync(
                    () => _adeHelper.UpdateTaskStateAsync(taskId, metrics, token),
                    _retryCount,
                    token).ConfigureAwait(false);

                stats.Commit(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FlushTaskStatsAsync failed. taskId={TaskId}", taskId);
            }
            finally
            {
                _flushSemaphore.Release();
            }
        }

        private async Task FlushProxyIpStateAsync(
            int taskId,
            AdTrafficProxyIpStateEntity stat,
            AdTrafficProxyIpStateSnapshot snapshot,
            Dictionary<string, long> metrics,
            CancellationToken token)
        {
            await _flushSemaphore.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await RetryAsync(
                    () => _adeHelper.UpdateProxyIpStateAsync(
                        taskId,
                        metrics,
                        snapshot.ConsumedIps.ToList(),
                        token),
                    _retryCount,
                    token).ConfigureAwait(false);

                stat.Commit(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FlushProxyIpAsync failed. taskId={TaskId}", taskId);
            }
            finally
            {
                _flushSemaphore.Release();
            }
        }

        private async Task FlushHostStateAsync(
            AdTrafficTaskStateSnapshot delta,
            Dictionary<string, long> metrics,
            CancellationToken token)
        {
            await _flushSemaphore.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await RetryAsync(
                    () => _adeHelper.UpdateHostStateAsync(metrics, token),
                    _retryCount,
                    token).ConfigureAwait(false);

                _hostTaskStates.Commit(delta);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FlushTotalStatsAsync failed.");
            }
            finally
            {
                _flushSemaphore.Release();
            }
        }


        #endregion

        #region Retry / Stop Helpers

        private async Task RetryAsync(Func<Task> func, int retryCount, CancellationToken token)
        {
            Exception? last = null;

            for (int attempt = 0; attempt <= retryCount; attempt++)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    await func().ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;

                    if (attempt >= retryCount)
                        break;

                    await Task.Delay(500, token).ConfigureAwait(false);
                }
            }

            throw last ?? new InvalidOperationException("RetryAsync failed with unknown error.");
        }

        private async Task WaitConsumersDrainAsync()
        {
            var tasks = new List<Task>(3);

            if (_processTaskQueue != null) tasks.Add(_processTaskQueue);
            if (_processIpQueue != null) tasks.Add(_processIpQueue);

            if (tasks.Count > 0)
                await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private async Task WaitBackgroundTasksAsync()
        {
            var tasks = new List<Task>(4);

            if (_processTaskQueue != null) tasks.Add(_processTaskQueue);
            if (_processIpQueue != null) tasks.Add(_processIpQueue);
            if (_flushLoopTask != null) tasks.Add(_flushLoopTask);

            if (tasks.Count == 0)
                return;

            foreach (var task in tasks)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }
        }

        #endregion

        #region Baseline Init

        private async Task EnsureTaskBaselineAsync(int taskId, double taskCtr)
        {
            var key = GetClickHourKey(taskId);

            if (_taskGlobalBaseline.ContainsKey(key))
            {
                _taskClickRates[key] = taskCtr;
                return;
            }

            var gate = _baselineInitLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);

            try
            {
                if (_taskGlobalBaseline.ContainsKey(key))
                {
                    _taskClickRates[key] = taskCtr;
                    return;
                }

                var resp = await _adeHelper.GetTaskStatusAsync(taskId).ConfigureAwait(false);

                var globalStats = new AdTrafficTaskStateEntity();
                if (resp != null)
                {
                    globalStats.Start = resp.SelectToken("data.start")?.Value<long>() ?? 0;
                    globalStats.DSP = resp.SelectToken("data.dsp")?.Value<long>() ?? 0;
                    globalStats.Clickthrough = resp.SelectToken("data.click")?.Value<long>() ?? 0;
                }

                _taskGlobalBaseline[key] = globalStats;
                _taskClickRates[key] = taskCtr;
            }
            finally
            {
                gate.Release();
            }
        }
        #endregion

        #region 时间缓存（UTC + 北京时间）

        private static string? _cachedHourKey;
        private static long _cachedHourTicks;

        private static readonly long HourTicks = TimeSpan.TicksPerHour;
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

        private static string GetHourKey()
        {
            var utcNow = DateTime.UtcNow;
            var currentHourTicks = utcNow.Ticks / HourTicks * HourTicks;

            var cachedTicks = Volatile.Read(ref _cachedHourTicks);
            var cachedKey = Volatile.Read(ref _cachedHourKey);

            if (cachedKey != null && cachedTicks == currentHourTicks)
                return cachedKey;

            var beijingTime = new DateTime(currentHourTicks, DateTimeKind.Utc).Add(BeijingOffset);
            var newKey = beijingTime.ToString("yyyyMMddHH");

            Volatile.Write(ref _cachedHourKey, newKey);
            Volatile.Write(ref _cachedHourTicks, currentHourTicks);

            return newKey;
        }

        #endregion

        #region 本地统计

        private AdTrafficLocalHourTaskState _localStats = new AdTrafficLocalHourTaskState(GetHourKey());

        private static readonly IReadOnlyDictionary<string, long> EmptyDict =
            new Dictionary<string, long>();

        public void AddLocalMetric(int taskId, string name, long value = 1)
        {
            if (taskId == 0 || string.IsNullOrWhiteSpace(name))
                return;

            var hour = GetHourKey();

            while (true)
            {
                var current = _localStats;

                if (current.HourKey == hour)
                {
                    var taskDict = current.BufferData.GetOrAdd(taskId,
                        _ => new ConcurrentDictionary<string, long>(Environment.ProcessorCount, 16));

                    taskDict.AddOrUpdate(name, value, (_, old) => old + value);
                    return;
                }

                var newStats = new AdTrafficLocalHourTaskState(hour);

                if (Interlocked.CompareExchange(ref _localStats, newStats, current) == current)
                {
                    var taskDict = newStats.BufferData.GetOrAdd(taskId,
                        _ => new ConcurrentDictionary<string, long>(Environment.ProcessorCount, 16));

                    taskDict.TryAdd(name, value);
                    return;
                }
            }
        }

        public IReadOnlyDictionary<string, long> GetAllLocalMetric(int taskId)
        {
            if (taskId == 0)
                return EmptyDict;

            var hour = GetHourKey();
            var stats = _localStats;

            if (stats.HourKey != hour)
                return EmptyDict;

            return stats.BufferData.TryGetValue(taskId, out var dict)
                ? dict
                : EmptyDict;
        }

        public long GetLocalMetric(int taskId, string name)
        {
            if (taskId == 0 || string.IsNullOrWhiteSpace(name))
                return 0;

            var hour = GetHourKey();
            var stats = _localStats;

            if (stats.HourKey != hour)
                return 0;

            if (!stats.BufferData.TryGetValue(taskId, out var dict))
                return 0;

            return dict.TryGetValue(name, out var value) ? value : 0;
        }

        public Dictionary<string, long> GetLocalMetrics(int taskId, params string[] names)
        {
            var result = new Dictionary<string, long>();

            if (taskId == 0 || names == null || names.Length == 0)
                return result;

            foreach (var name in names)
                result[name] = 0;

            var hour = GetHourKey();
            var stats = _localStats;

            if (stats.HourKey != hour)
                return result;

            if (!stats.BufferData.TryGetValue(taskId, out var dict))
                return result;

            foreach (var name in names)
            {
                result[name] = dict.TryGetValue(name, out var value) ? value : 0;
            }

            return result;
        }

        public double GetStatRatio(int taskId, params string[] names)
        {
            if (taskId == 0 || names == null || names.Length == 0)
                return 0;

            var hour = GetHourKey();
            var stats = _localStats;

            if (stats.HourKey != hour)
                return 0;

            if (!stats.BufferData.TryGetValue(taskId, out var dict))
                return 0;

            var set = new HashSet<string>(names);

            long total = 0;
            long part = 0;

            foreach (var kv in dict)
            {
                total += kv.Value;
                if (set.Contains(kv.Key))
                    part += kv.Value;
            }

            return total == 0 ? 0 : (double)part / total;
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _state) == 4)
                    return;
            }
            finally
            {
                _lifecycleLock.Release();
            }

            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _state, 4);

                _flushSemaphore.Dispose();
                _lifecycleLock.Dispose();

                foreach (var gate in _baselineInitLocks.Values)
                    gate.Dispose();
            }
        }

        #endregion
    }
}
