using System.Diagnostics;

namespace MainClient.UiTask
{
    public enum RunnerState
    {
        Stopped = 0,
        Running = 1,
        Stopping = 2,
        Faulted = 3
    }

    public sealed class RunnerLogEvent
    {
        public DateTime Time { get; init; } = DateTime.Now;
        public string Source { get; init; } = "";
        public string Message { get; init; } = "";
        public Exception? Exception { get; init; }
    }

    public sealed class PeriodicTaskSnapshot
    {
        public string Name { get; init; } = "";
        public TimeSpan Interval { get; init; }
        public bool Enabled { get; init; }
        public bool IsRunning { get; init; }
        public bool IsCircuitBroken { get; init; }

        public DateTime? LastStartTime { get; init; }
        public DateTime? LastEndTime { get; init; }
        public TimeSpan? LastElapsed { get; init; }

        public long SuccessCount { get; init; }
        public long FailureCount { get; init; }
        public int ConsecutiveFailureCount { get; init; }
        public long SkipCount { get; init; }
        public string? LastError { get; init; }
    }

    public class UiTaskRunner
    {
        private sealed class PeriodicDefinition
        {
            public string Name { get; init; } = "";
            public TimeSpan Interval { get; init; }
            public Func<CancellationToken, Task> OnTick { get; init; } = default!;

            /// <summary>
            /// 上一次未完成时，本次是否跳过
            /// </summary>
            public bool SkipIfRunning { get; init; } = true;

            /// <summary>
            /// 单次执行超时，null 表示不限制
            /// </summary>
            public TimeSpan? Timeout { get; init; }

            /// <summary>
            /// 连续失败达到阈值后自动熔断，0 表示不启用
            /// </summary>
            public int CircuitBreakThreshold { get; init; } = 0;

            /// <summary>
            /// 熔断后冷却时间，null 表示永久暂停直到下次 Start
            /// </summary>
            public TimeSpan? CircuitBreakCooldown { get; init; }

            public bool Enabled { get; set; } = true;
        }

        private sealed class PeriodicLoopContext
        {
            public required PeriodicDefinition Definition { get; init; }
            public required PeriodicTimer Timer { get; init; }
            public required CancellationTokenSource Cts { get; init; }

            // 从 init 改成 set，避免先声明闭包变量、后赋值的时序风险
            public Task LoopTask { get; set; } = Task.CompletedTask;

            /// <summary>
            /// 0=空闲 1=执行中
            /// </summary>
            public int RunningFlag;

            public DateTime? LastStartTime;
            public DateTime? LastEndTime;
            public TimeSpan? LastElapsed;

            public long SuccessCount;
            public long FailureCount;
            public int ConsecutiveFailureCount;
            public long SkipCount;

            public string? LastError;

            public DateTime? CircuitBrokenUntil;
            public bool IsCircuitBroken;
        }

        private readonly List<PeriodicDefinition> _periodicDefinitions = new();
        private readonly List<PeriodicLoopContext> _periodicLoops = new();

        private readonly Stopwatch _stopwatch = new();
        private readonly Func<CancellationToken, Task> _runTask;
        private readonly object _sync = new();

        private CancellationTokenSource? _cts;
        private Task? _runLoopTask;
        private int _stopInternalEntered = 0;

        public event Action<RunnerState>? StateChanged;
        public event Action<Exception>? Faulted;
        public event Action<RunnerLogEvent>? LogEmitted;

        public RunnerState State { get; private set; } = RunnerState.Stopped;
        public TimeSpan RunElapsed => _stopwatch.Elapsed;

        public UiTaskRunner(Func<CancellationToken, Task> runTask)
        {
            _runTask = runTask ?? throw new ArgumentNullException(nameof(runTask));
        }

        public void Start()
        {
            lock (_sync)
            {
                if (State is RunnerState.Running or RunnerState.Stopping)
                    return;

                _stopInternalEntered = 0;

                _stopwatch.Reset();
                _stopwatch.Start();

                _cts = new CancellationTokenSource();
                var rootToken = _cts.Token;

                ChangeState(RunnerState.Running);
                WriteLog("Runner", "任务启动");
                StartPeriodicLoops(rootToken);

                _runLoopTask = Task.Run(async () =>
                {
                    try
                    {
                        await _runTask(rootToken).ConfigureAwait(false);

                        lock (_sync)
                        {
                            if (State == RunnerState.Running)
                                ChangeState(RunnerState.Stopped);
                        }

                        WriteLog("Runner", "主任务正常结束");
                    }
                    catch (OperationCanceledException)
                    {
                        lock (_sync)
                        {
                            if (State != RunnerState.Faulted)
                                ChangeState(RunnerState.Stopped);
                        }

                        WriteLog("Runner", "主任务已取消");
                    }
                    catch (Exception ex)
                    {
                        lock (_sync)
                        {
                            ChangeState(RunnerState.Faulted);
                        }

                        WriteLog("Runner", "主任务异常", ex);
                        RaiseFaulted(ex);
                    }
                    finally
                    {
                        await StopInternalAsync().ConfigureAwait(false);
                    }
                });
            }
        }

        public async Task StopAsync()
        {
            Task? runnerTask;

            lock (_sync)
            {
                if (State == RunnerState.Stopped)
                    return;

                if (State != RunnerState.Faulted)
                    ChangeState(RunnerState.Stopping);

                try
                {
                    _cts?.Cancel();
                }
                catch
                {
                }

                runnerTask = _runLoopTask;
            }

            WriteLog("Runner", "收到停止请求");

            if (runnerTask != null)
            {
                try
                {
                    await runnerTask.ConfigureAwait(false);
                }
                catch
                {
                }
            }

            await StopInternalAsync().ConfigureAwait(false);
        }

        private void StartPeriodicLoops(CancellationToken rootToken)
        {
            foreach (var def in _periodicDefinitions)
            {
                var timer = new PeriodicTimer(def.Interval);
                var cts = CancellationTokenSource.CreateLinkedTokenSource(rootToken);

                var ctx = new PeriodicLoopContext
                {
                    Definition = def,
                    Timer = timer,
                    Cts = cts,
                    RunningFlag = 0
                };

                ctx.LoopTask = Task.Run(async () =>
                {
                    try
                    {
                        while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
                        {
                            if (!def.Enabled)
                                continue;

                            if (ctx.IsCircuitBroken)
                            {
                                if (ctx.CircuitBrokenUntil.HasValue &&
                                    DateTime.Now >= ctx.CircuitBrokenUntil.Value)
                                {
                                    ctx.IsCircuitBroken = false;
                                    ctx.CircuitBrokenUntil = null;
                                    ctx.ConsecutiveFailureCount = 0;
                                    WriteLog(def.Name, "熔断冷却结束，恢复执行");
                                }
                                else
                                {
                                    continue;
                                }
                            }

                            if (def.SkipIfRunning && Interlocked.Exchange(ref ctx.RunningFlag, 1) == 1)
                            {
                                Interlocked.Increment(ref ctx.SkipCount);
                                WriteLog(def.Name, "跳过本轮执行：上一次尚未完成");
                                continue;
                            }

                            try
                            {
                                await ExecutePeriodicTickAsync(ctx, cts.Token).ConfigureAwait(false);
                            }
                            finally
                            {
                                if (def.SkipIfRunning)
                                    Volatile.Write(ref ctx.RunningFlag, 0);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (Exception ex)
                    {
                        var wrapped = new Exception(
                            $"周期调度循环异常: {def.Name}, Interval={def.Interval.TotalMilliseconds}ms", ex);

                        WriteLog(def.Name, "周期调度循环异常", wrapped);
                        RaiseFaulted(wrapped);
                    }
                }, cts.Token);

                _periodicLoops.Add(ctx);

                WriteLog(def.Name,
                    $"周期任务已注册，间隔={def.Interval.TotalMilliseconds}ms, SkipIfRunning={def.SkipIfRunning}, Timeout={def.Timeout?.TotalMilliseconds}ms");
            }
        }

        private async Task ExecutePeriodicTickAsync(PeriodicLoopContext ctx, CancellationToken rootToken)
        {
            var def = ctx.Definition;
            var sw = Stopwatch.StartNew();

            ctx.LastStartTime = DateTime.Now;
            WriteLog(def.Name, "开始执行");

            try
            {
                if (def.Timeout.HasValue && def.Timeout.Value > TimeSpan.Zero)
                {
                    using var timeoutCts = new CancellationTokenSource(def.Timeout.Value);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(rootToken, timeoutCts.Token);

                    try
                    {
                        await def.OnTick(linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !rootToken.IsCancellationRequested)
                    {
                        throw new TimeoutException($"周期任务执行超时: {def.Name}, Timeout={def.Timeout.Value.TotalMilliseconds}ms");
                    }
                }
                else
                {
                    await def.OnTick(rootToken).ConfigureAwait(false);
                }

                sw.Stop();

                ctx.LastEndTime = DateTime.Now;
                ctx.LastElapsed = sw.Elapsed;
                ctx.LastError = null;
                ctx.ConsecutiveFailureCount = 0;
                Interlocked.Increment(ref ctx.SuccessCount);

                WriteLog(def.Name, $"执行成功，耗时={sw.ElapsedMilliseconds}ms");
            }
            catch (OperationCanceledException) when (rootToken.IsCancellationRequested)
            {
                sw.Stop();

                ctx.LastEndTime = DateTime.Now;
                ctx.LastElapsed = sw.Elapsed;

                WriteLog(def.Name, "执行取消");
            }
            catch (Exception ex)
            {
                sw.Stop();

                ctx.LastEndTime = DateTime.Now;
                ctx.LastElapsed = sw.Elapsed;
                ctx.LastError = ex.Message;

                Interlocked.Increment(ref ctx.FailureCount);
                ctx.ConsecutiveFailureCount++;

                WriteLog(def.Name,
                    $"执行失败，耗时={sw.ElapsedMilliseconds}ms，连续失败={ctx.ConsecutiveFailureCount}",
                    ex);

                RaiseFaulted(new Exception(
                    $"周期任务执行异常: {def.Name}, Interval={def.Interval.TotalMilliseconds}ms", ex));

                TryCircuitBreak(ctx);
            }
        }

        private void TryCircuitBreak(PeriodicLoopContext ctx)
        {
            var def = ctx.Definition;

            if (def.CircuitBreakThreshold <= 0)
                return;

            if (ctx.ConsecutiveFailureCount < def.CircuitBreakThreshold)
                return;

            ctx.IsCircuitBroken = true;

            if (def.CircuitBreakCooldown.HasValue && def.CircuitBreakCooldown.Value > TimeSpan.Zero)
            {
                ctx.CircuitBrokenUntil = DateTime.Now.Add(def.CircuitBreakCooldown.Value);

                WriteLog(def.Name,
                    $"已触发熔断，将在 {ctx.CircuitBrokenUntil:yyyy-MM-dd HH:mm:ss} 后恢复");
            }
            else
            {
                ctx.CircuitBrokenUntil = null;
                def.Enabled = false;

                WriteLog(def.Name, "已触发熔断，任务已永久暂停（本次运行周期内）");
            }
        }

        private async Task StopInternalAsync()
        {
            if (Interlocked.Exchange(ref _stopInternalEntered, 1) == 1)
                return;

            List<PeriodicLoopContext> periodicLoopsSnapshot;
            CancellationTokenSource? rootCts;
            RunnerState currentState;

            lock (_sync)
            {
                if (_stopwatch.IsRunning)
                    _stopwatch.Stop();

                periodicLoopsSnapshot = _periodicLoops.ToList();
                rootCts = _cts;
                currentState = State;
            }

            foreach (var loop in periodicLoopsSnapshot)
            {
                try { loop.Cts.Cancel(); } catch { }
            }

            try
            {
                var allLoopTasks = periodicLoopsSnapshot.Select(x => x.LoopTask).ToArray();
                if (allLoopTasks.Length > 0)
                    await Task.WhenAll(allLoopTasks).ConfigureAwait(false);
            }
            catch
            {
            }

            foreach (var loop in periodicLoopsSnapshot)
            {
                try { loop.Timer.Dispose(); } catch { }
                try { loop.Cts.Dispose(); } catch { }
            }

            lock (_sync)
            {
                _periodicLoops.Clear();

                try { rootCts?.Dispose(); } catch { }

                _cts = null;
                _runLoopTask = null;

                if (State == RunnerState.Stopping)
                {
                    ChangeState(RunnerState.Stopped);
                }
                else if (currentState == RunnerState.Running)
                {
                    ChangeState(RunnerState.Stopped);
                }
            }

            WriteLog("Runner", "资源清理完成");
        }

        public IReadOnlyList<PeriodicTaskSnapshot> GetPeriodicSnapshots()
        {
            lock (_sync)
            {
                return _periodicLoops.Select(x => new PeriodicTaskSnapshot
                {
                    Name = x.Definition.Name,
                    Interval = x.Definition.Interval,
                    Enabled = x.Definition.Enabled,
                    IsRunning = Volatile.Read(ref x.RunningFlag) == 1,
                    IsCircuitBroken = x.IsCircuitBroken,
                    LastStartTime = x.LastStartTime,
                    LastEndTime = x.LastEndTime,
                    LastElapsed = x.LastElapsed,
                    SuccessCount = x.SuccessCount,
                    FailureCount = x.FailureCount,
                    ConsecutiveFailureCount = x.ConsecutiveFailureCount,
                    SkipCount = x.SkipCount,
                    LastError = x.LastError
                }).ToList();
            }
        }

        public void EnablePeriodicTask(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException(nameof(name));

            lock (_sync)
            {
                var def = _periodicDefinitions.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (def == null)
                    throw new InvalidOperationException($"未找到周期任务: {name}");

                def.Enabled = true;

                var ctx = _periodicLoops.FirstOrDefault(x => x.Definition == def);
                if (ctx != null)
                {
                    ctx.IsCircuitBroken = false;
                    ctx.CircuitBrokenUntil = null;
                    ctx.ConsecutiveFailureCount = 0;
                }
            }

            WriteLog(name, "周期任务已启用");
        }

        public void DisablePeriodicTask(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException(nameof(name));

            lock (_sync)
            {
                var def = _periodicDefinitions.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (def == null)
                    throw new InvalidOperationException($"未找到周期任务: {name}");

                def.Enabled = false;
            }

            WriteLog(name, "周期任务已禁用");
        }

        public void SetPeriodicAction(TimeSpan interval, Func<Task> onTick)
        {
            if (interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interval));
            if (onTick == null)
                throw new ArgumentNullException(nameof(onTick));

            lock (_sync)
            {
                EnsureCanRegisterPeriodicTask();

                _periodicDefinitions.Add(new PeriodicDefinition
                {
                    Interval = interval,
                    Name = $"Periodic@{interval.TotalMilliseconds:0}ms",
                    SkipIfRunning = true,
                    OnTick = _ => onTick()
                });
            }
        }

        public void SetPeriodicAction(
            TimeSpan interval,
            Func<CancellationToken, Task> onTick,
            string? name = null,
            bool skipIfRunning = true,
            TimeSpan? timeout = null,
            int circuitBreakThreshold = 0,
            TimeSpan? circuitBreakCooldown = null)
        {
            if (interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interval));
            if (onTick == null)
                throw new ArgumentNullException(nameof(onTick));
            if (timeout.HasValue && timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            if (circuitBreakThreshold < 0)
                throw new ArgumentOutOfRangeException(nameof(circuitBreakThreshold));

            lock (_sync)
            {
                EnsureCanRegisterPeriodicTask();

                var finalName = string.IsNullOrWhiteSpace(name)
                    ? $"Periodic@{interval.TotalMilliseconds:0}ms"
                    : name!;

                if (_periodicDefinitions.Any(x => x.Name.Equals(finalName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"周期任务名称重复: {finalName}");

                _periodicDefinitions.Add(new PeriodicDefinition
                {
                    Interval = interval,
                    Name = finalName,
                    SkipIfRunning = skipIfRunning,
                    Timeout = timeout,
                    CircuitBreakThreshold = circuitBreakThreshold,
                    CircuitBreakCooldown = circuitBreakCooldown,
                    OnTick = onTick
                });
            }
        }

        private void EnsureCanRegisterPeriodicTask()
        {
            if (State != RunnerState.Stopped)
                throw new InvalidOperationException("运行中不能新增周期任务，请在 Start() 前配置。");
        }

        private void ChangeState(RunnerState state)
        {
            if (State == state)
                return;

            State = state;

            try
            {
                StateChanged?.Invoke(state);
            }
            catch
            {
            }
        }

        private void RaiseFaulted(Exception ex)
        {
            try
            {
                Faulted?.Invoke(ex);
            }
            catch
            {
            }
        }

        private void WriteLog(string source, string message, Exception? ex = null)
        {
            //try
            //{
            //    LogEmitted?.Invoke(new RunnerLogEvent
            //    {
            //        Source = source,
            //        Message = message,
            //        Exception = ex
            //    });
            //}
            //catch
            //{
            //}
        }
    }
}
