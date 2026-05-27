

namespace MainClient.Ipc
{
    using System.Diagnostics;
    using System.IO.Pipes;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Collections.Concurrent;


    public sealed class CefClientSession : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly string _exePath;
        private readonly string _pipeName;
        private readonly string? _consumerId;
        private readonly TimeSpan _startTimeout;
        private readonly CancellationTokenSource _disposeCts = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        private readonly NamedPipeServerStream _pipeServer;

        private StreamReader? _reader;
        private StreamWriter? _writer;
        private Task? _readLoopTask;

        private readonly TaskCompletionSource<bool> _readyTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly ConcurrentDictionary<string, TaskCompletionSource<PipeEnvelope>> _pending =
            new(StringComparer.OrdinalIgnoreCase);

        public Process? Process { get; private set; }
        public string PipeName => _pipeName;

        public event Func<string, Task>? OnLog;
        public event Func<PipeEnvelope, Task>? OnBrowserResult;
        public event Func<PipeEnvelope, Task>? OnBrowserScreenshot;
        public event Func<PipeEnvelope, Task>? OnBrowserStatus;

        private static Task InvokeHandlerSafeAsync(Func<Task> handler)
        {
            return Task.Run(async () =>
            {
                try
                {
                    await handler();
                }
                catch
                {
                }
            });
        }

        public CefClientSession(
            string exePath,
            TimeSpan? startTimeout = null,
            string? consumerId = null)
        {
            _exePath = exePath;
            _consumerId = string.IsNullOrWhiteSpace(consumerId) ? null : consumerId;
            _startTimeout = startTimeout ?? TimeSpan.FromSeconds(10);
            _pipeName = $"cefclient_pipe_{Environment.ProcessId}_{Guid.NewGuid():N}";

            _pipeServer = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (!File.Exists(_exePath))
                throw new FileNotFoundException($"找不到 {Path.GetFileName(_exePath)}", _exePath);

            var psi = new ProcessStartInfo
            {
                FileName = _exePath,
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Path.GetDirectoryName(_exePath)!
            };

            psi.ArgumentList.Add($"--pipe-name={_pipeName}");
            if (!string.IsNullOrWhiteSpace(_consumerId))
                psi.ArgumentList.Add($"--consumer-id={_consumerId}");

            var process = new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true
            };

            if (!process.Start())
                throw new InvalidOperationException($"启动 {Path.GetFileName(_exePath)} 失败");

            Process = process;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _disposeCts.Token);
            cts.CancelAfter(_startTimeout);

            try
            {
                var connectTask = _pipeServer.WaitForConnectionAsync(cts.Token);
                var exitTask = process.WaitForExitAsync(CancellationToken.None);
                var completedTask = await Task.WhenAny(connectTask, exitTask);
                if (completedTask == exitTask)
                {
                    throw new InvalidOperationException(
                        $"{Path.GetFileName(_exePath)} 在连接管道前已退出，ExitCode={SafeGetExitCode(process)}, pipe={_pipeName}");
                }

                await connectTask;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"等待 {Path.GetFileName(_exePath)} 连接管道超时，timeout={(int)_startTimeout.TotalSeconds}s, pipe={_pipeName}, process={SafeGetProcessState(process)}");
            }

            _reader = new StreamReader(_pipeServer, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            _writer = new StreamWriter(_pipeServer, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true
            };

            _readLoopTask = Task.Run(() => ReadLoopAsync(_disposeCts.Token));

            using var reg = cts.Token.Register(() => _readyTcs.TrySetCanceled(cts.Token));
            try
            {
                await _readyTcs.Task;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"等待 {Path.GetFileName(_exePath)} ready 消息超时，timeout={(int)_startTimeout.TotalSeconds}s, pipe={_pipeName}, process={SafeGetProcessState(process)}");
            }
        }


        private static string SafeGetExitCode(Process process)
        {
            try
            {
                return process.HasExited ? process.ExitCode.ToString() : "running";
            }
            catch
            {
                return "unknown";
            }
        }

        private static string SafeGetProcessState(Process process)
        {
            try
            {
                return process.HasExited
                    ? $"exited: ExitCode={process.ExitCode}"
                    : $"running: Pid={process.Id}";
            }
            catch
            {
                return "unknown";
            }
        }

        public async Task StartTaskAsync(string taskId, JsonNode? payload, CancellationToken cancellationToken = default)
        {
            var resp = await SendAndWaitAsync(
                new PipeEnvelope
                {
                    Type = "start",
                    TaskId = taskId,
                    Payload = payload
                },
                expectedType: "started",
                key: $"started:{taskId}",
                cancellationToken: cancellationToken);

            if (resp.Success != true)
                throw new InvalidOperationException(resp.Message ?? "start failed");
        }

        public async Task CreateBrowserAsync(
            string taskId,
            string browserId,
            CancellationToken cancellationToken = default)
        {
            var resp = await SendAndWaitAsync(
                new PipeEnvelope
                {
                    Type = "createBrowser",
                    TaskId = taskId,
                    BrowserId = browserId
                },
                expectedType: "browserCreated",
                key: $"browserCreated:{browserId}",
                cancellationToken: cancellationToken);

            if (resp.Success != true)
                throw new InvalidOperationException(resp.Message ?? $"createBrowser failed: {browserId}");
        }

        public async Task CreateBrowserNoWaitAsync(
            string taskId,
            string browserId,
            JsonNode? payload,
            CancellationToken cancellationToken = default)
        {
            await SendAsync(
                new PipeEnvelope
                {
                    Type = "createBrowser",
                    TaskId = taskId,
                    BrowserId = browserId,
                    Payload = payload
                },
                cancellationToken);
        }

        public async Task<BrowserRunResponse> RunBrowserAsync(
            string taskId,
            string browserId,
            JsonNode? payload,
            CancellationToken cancellationToken = default)
        {
            var resp = await SendAndWaitAsync(
                new PipeEnvelope
                {
                    Type = "runBrowser",
                    TaskId = taskId,
                    BrowserId = browserId,
                    Payload = payload
                },
                expectedType: "browserResult",
                key: $"browserResult:{browserId}",
                cancellationToken: cancellationToken);

            return new BrowserRunResponse
            {
                Success = resp.Success ?? false,
                Message = resp.Message ?? "",
                Data = resp.Data
            };
        }

        public async Task RunBrowserNoWaitAsync(
            string taskId,
            string browserId,
            JsonNode? payload,
            CancellationToken cancellationToken = default)
        {
            await SendAsync(
                new PipeEnvelope
                {
                    Type = "runBrowser",
                    TaskId = taskId,
                    BrowserId = browserId,
                    Payload = payload
                },
                cancellationToken);
        }

        public async Task RemoveBrowserAsync(
            string taskId,
            string browserId,
            CancellationToken cancellationToken = default)
        {
            var resp = await SendAndWaitAsync(
                new PipeEnvelope
                {
                    Type = "removeBrowser",
                    TaskId = taskId,
                    BrowserId = browserId
                },
                expectedType: "browserRemoved",
                key: $"browserRemoved:{browserId}",
                cancellationToken: cancellationToken);

            if (resp.Success != true)
                throw new InvalidOperationException(resp.Message ?? $"removeBrowser failed: {browserId}");
        }

        public async Task SendExitAsync(CancellationToken cancellationToken = default)
        {
            await SendAsync(new PipeEnvelope
            {
                Type = "exit"
            }, cancellationToken);
        }

        private async Task<PipeEnvelope> SendAndWaitAsync(
            PipeEnvelope request,
            string expectedType,
            string key,
            CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<PipeEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!_pending.TryAdd(key, tcs))
                throw new InvalidOperationException($"重复等待响应: {key}");

            try
            {
                using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
                await SendAsync(request, cancellationToken);
                var resp = await tcs.Task;

                if (!string.Equals(resp.Type, expectedType, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"收到意外响应类型: {resp.Type}, 期待: {expectedType}");

                return resp;
            }
            finally
            {
                _pending.TryRemove(key, out _);
            }
        }

        private async Task SendAsync(PipeEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (_writer == null)
                throw new InvalidOperationException("管道尚未建立");

            var json = JsonSerializer.Serialize(envelope, JsonOptions);

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _writer.WriteLineAsync(json);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && _reader != null)
                {
                    var line = await _reader.ReadLineAsync();
                    if (line == null)
                        break;

                    PipeEnvelope? msg;
                    try
                    {
                        msg = JsonSerializer.Deserialize<PipeEnvelope>(line, JsonOptions);
                    }
                    catch
                    {
                        continue;
                    }

                    if (msg == null)
                        continue;

                    if (string.Equals(msg.Type, "ready", StringComparison.OrdinalIgnoreCase))
                    {
                        _readyTcs.TrySetResult(true);
                        continue;
                    }

                    if (string.Equals(msg.Type, "log", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(msg.Message) && OnLog != null)
                            _ = InvokeHandlerSafeAsync(() => OnLog.Invoke(msg.Message));
                        continue;
                    }

                    if (string.Equals(msg.Type, "browserResult", StringComparison.OrdinalIgnoreCase))
                    {
                        if (OnBrowserResult != null)
                        {
                            _ = InvokeHandlerSafeAsync(() => OnBrowserResult.Invoke(msg));
                        }
                    }

                    if (string.Equals(msg.Type, "browserScreenshot", StringComparison.OrdinalIgnoreCase))
                    {
                        if (OnBrowserScreenshot != null)
                        {
                            _ = InvokeHandlerSafeAsync(() => OnBrowserScreenshot.Invoke(msg));
                        }
                    }

                    if (string.Equals(msg.Type, "browserStatus", StringComparison.OrdinalIgnoreCase))
                    {
                        if (OnBrowserStatus != null)
                        {
                            _ = InvokeHandlerSafeAsync(() => OnBrowserStatus.Invoke(msg));
                        }
                    }

                    var key = BuildResponseKey(msg);
                    if (key != null && _pending.TryGetValue(key, out var waiter))
                    {
                        waiter.TrySetResult(msg);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _readyTcs.TrySetException(ex);

                foreach (var kv in _pending)
                {
                    kv.Value.TrySetException(ex);
                }
            }
            finally
            {
                if (!_readyTcs.Task.IsCompleted)
                    _readyTcs.TrySetException(new IOException("CefClient 管道断开"));

                foreach (var kv in _pending)
                {
                    kv.Value.TrySetException(new IOException("CefClient 管道断开"));
                }
            }
        }

        private static string? BuildResponseKey(PipeEnvelope msg)
        {
            return msg.Type switch
            {
                "started" => $"started:{msg.TaskId}",
                "browserCreated" => $"browserCreated:{msg.BrowserId}",
                "browserResult" => $"browserResult:{msg.BrowserId}",
                "browserRemoved" => $"browserRemoved:{msg.BrowserId}",
                _ => null
            };
        }

        public async Task CloseGracefullyAsync(TimeSpan timeout)
        {
            try
            {
                await SendExitAsync(CancellationToken.None);
            }
            catch
            {
            }

            if (Process == null)
                return;

            try
            {
                using var cts = new CancellationTokenSource(timeout);
                await Process.WaitForExitAsync(cts.Token);
            }
            catch
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { _disposeCts.Cancel(); } catch { }

            try
            {
                await SendExitAsync();
            }
            catch
            {
            }

            try
            {
                if (_readLoopTask != null)
                    await _readLoopTask;
            }
            catch
            {
            }

            try
            {
                if (Process is { HasExited: false })
                    Process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            try
            {
                if (Process != null)
                    await Process.WaitForExitAsync();
            }
            catch
            {
            }

            _reader?.Dispose();
            _writer?.Dispose();
            _pipeServer.Dispose();
            _writeLock.Dispose();
            _disposeCts.Dispose();
            Process?.Dispose();
        }
    }

}
