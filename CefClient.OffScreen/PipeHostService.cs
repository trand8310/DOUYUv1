using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CefClient;

public sealed class PipeHostService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _pipeName;
    private readonly OffScreenBrowserHost _browserHost;
    private readonly CancellationTokenSource _cts = new();

    private NamedPipeClientStream? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Task> _runTasks = new();

    private string? _taskId;
    private System.Text.Json.Nodes.JsonNode? _taskPayload;

    public PipeHostService(string pipeName, OffScreenBrowserHost browserHost)
    {
        _pipeName = pipeName;
        _browserHost = browserHost;
        _browserHost.BrowserLog += message => _ = SendBrowserLogSafeAsync(message);
        _browserHost.BrowserScreenshot += (browserId, bytes) => _ = SendBrowserScreenshotSafeAsync(browserId, bytes);
        _browserHost.BrowserStatus += SendBrowserStatusSafeAsync;
    }

    private async Task SendBrowserScreenshotSafeAsync(string browserId, byte[] screenshotBytes)
    {
        try
        {
            await SendAsync(new PipeEnvelope
            {
                Type = "browserScreenshot",
                TaskId = _taskId,
                BrowserId = browserId,
                Success = true,
                Message = "screenshot captured",
                Data = new System.Text.Json.Nodes.JsonObject
                {
                    ["contentType"] = "image/png",
                    ["base64"] = Convert.ToBase64String(screenshotBytes),
                    ["byteLength"] = screenshotBytes.Length,
                    ["capturedAt"] = DateTimeOffset.Now.ToString("O")
                }
            }, CancellationToken.None);
        }
        catch
        {
        }
    }

    private async Task SendBrowserLogSafeAsync(string message)
    {
        try
        {
            await SendLogAsync(message, CancellationToken.None);
        }
        catch
        {
        }
    }

    private async Task SendBrowserStatusSafeAsync(string browserId, BrowserRunStatus status, CancellationToken cancellationToken)
    {
        try
        {
            await SendBrowserStatusAsync(
                browserId,
                status.Stage,
                status.Success,
                status.Message,
                cancellationToken,
                status.Data);
        }
        catch
        {
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _client.ConnectAsync(5000, cancellationToken);

        _reader = new StreamReader(_client, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        _writer = new StreamWriter(_client, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true
        };

        await SendAsync(new PipeEnvelope
        {
            Type = "ready"
        }, cancellationToken);

        await SendLogAsync($"Pipe connected. pipe={_pipeName}", cancellationToken);
    }

    public async Task RunLoopAsync(CancellationToken cancellationToken = default)
    {
        if (_reader == null)
            throw new InvalidOperationException("未启动");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        var token = linkedCts.Token;

        while (!token.IsCancellationRequested)
        {
            var line = await _reader.ReadLineAsync();
            if (line == null)
                break;

            PipeEnvelope? req;
            try
            {
                req = JsonSerializer.Deserialize<PipeEnvelope>(line, JsonOptions);
            }
            catch
            {
                continue;
            }

            if (req == null)
                continue;

            switch (req.Type)
            {
                case "start":
                    _taskId = req.TaskId;
                    _taskPayload = req.Payload;
                    await SendAsync(new PipeEnvelope
                    {
                        Type = "started",
                        TaskId = _taskId,
                        Success = true
                    }, token);
                    break;

                case "createBrowser":
                    await HandleCreateBrowserAsync(req, token);
                    break;

                case "runBrowser":
                    _ = HandleRunBrowserAsync(req, token);
                    break;

                case "removeBrowser":
                    await HandleRemoveBrowserAsync(req, token);
                    break;

                case "exit":
                    await WaitRunTasksAsync(TimeSpan.FromSeconds(10));
                    await _browserHost.RemoveAllBrowsersAsync();
                    return;
            }
        }
    }

    private async Task HandleCreateBrowserAsync(PipeEnvelope req, CancellationToken token)
    {
        var ok = await _browserHost.CreateBrowserAsync(req.BrowserId!, token);
        await SendLogAsync($"CreateBrowserAsync done. browserId={req.BrowserId}, success={ok}", token);

        await SendAsync(new PipeEnvelope
        {
            Type = "browserCreated",
            TaskId = _taskId,
            BrowserId = req.BrowserId,
            Success = ok,
            Message = ok ? "created" : "create failed"
        }, token);

        await SendBrowserStatusAsync(
            req.BrowserId,
            stage: "created",
            success: ok,
            message: ok ? "browser created" : "browser create failed",
            cancellationToken: token);
    }

    private async Task HandleRunBrowserAsync(PipeEnvelope req, CancellationToken token)
    {
        var browserId = req.BrowserId!;
        var payload = req.Payload ?? _taskPayload;
        await SendLogAsync($"RunBrowserAsync start. browserId={browserId}", token);

        if (_runTasks.ContainsKey(browserId))
        {
            await SendLogAsync($"RunBrowserAsync skipped duplicated browserId={browserId}", token);
            return;
        }

        var runTask = Task.Run(async () =>
        {
            BrowserRunResult result;
            try
            {
                result = await _browserHost.RunBrowserAsync(browserId, payload, token);
                await SendLogAsync($"RunBrowserAsync finished. browserId={browserId}, success={result.Success}, msg={result.Message}", CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = new BrowserRunResult
                {
                    BrowserId = browserId,
                    Success = false,
                    Message = ex.Message
                };
                await SendLogAsync($"RunBrowserAsync exception. browserId={browserId}, msg={ex.Message}", CancellationToken.None);
            }

            if (result.Success)
            {
                await SendBrowserStatusAsync(
                    browserId,
                    stage: "opened",
                    success: true,
                    message: "page opened",
                    cancellationToken: CancellationToken.None,
                    data: result.Data);
            }

            var dataObj = result.Data as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
            dataObj["removedByCefClient"] = true;
            dataObj["osrOneShot"] = true;
            dataObj["disposedByRunAsync"] = true;
            result.Data = dataObj;
            await SendBrowserStatusAsync(
                browserId,
                stage: "disposed",
                success: true,
                message: "osr browser disposed by RunAsync",
                cancellationToken: CancellationToken.None,
                data: result.Data);

            await SendAsync(new PipeEnvelope
            {
                Type = "browserResult",
                TaskId = _taskId,
                BrowserId = browserId,
                Success = result.Success,
                Message = result.Message,
                Data = result.Data
            }, CancellationToken.None);
            await SendLogAsync($"browserResult sent. browserId={browserId}, success={result.Success}", CancellationToken.None);
        }, CancellationToken.None);

        if (!_runTasks.TryAdd(browserId, runTask))
        {
            await SendLogAsync($"RunBrowserAsync TryAdd failed, duplicated browserId={browserId}", token);
            return;
        }

        try
        {
            await runTask;
        }
        finally
        {
            _runTasks.TryRemove(browserId, out _);
        }
    }

    private async Task HandleRemoveBrowserAsync(PipeEnvelope req, CancellationToken token)
    {
        try
        {
            await _browserHost.RemoveBrowserFastAsync(req.BrowserId!);
            await SendLogAsync($"RemoveBrowserFastAsync done. browserId={req.BrowserId}", token);

            await SendAsync(new PipeEnvelope
            {
                Type = "browserRemoved",
                TaskId = _taskId,
                BrowserId = req.BrowserId,
                Success = true,
                Message = "removed"
            }, token);

            await SendBrowserStatusAsync(
                req.BrowserId,
                stage: "removed",
                success: true,
                message: "browser removed by main command",
                cancellationToken: token);
        }
        catch (Exception ex)
        {
            await SendLogAsync($"RemoveBrowserFastAsync failed. browserId={req.BrowserId}, msg={ex.Message}", token);
            await SendAsync(new PipeEnvelope
            {
                Type = "browserRemoved",
                TaskId = _taskId,
                BrowserId = req.BrowserId,
                Success = false,
                Message = ex.Message
            }, token);

            await SendBrowserStatusAsync(
                req.BrowserId,
                stage: "removeFailed",
                success: false,
                message: ex.Message,
                cancellationToken: token);
        }
    }

    public Task SendLogAsync(string message, CancellationToken cancellationToken = default)
    {
        return SendAsync(new PipeEnvelope
        {
            Type = "log",
            Message = message
        }, cancellationToken);
    }

    private async Task SendBrowserStatusAsync(
        string? browserId,
        string stage,
        bool success,
        string message,
        CancellationToken cancellationToken,
        System.Text.Json.Nodes.JsonNode? data = null)
    {
        var statusData = data as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        statusData["stage"] = stage;

        await SendAsync(new PipeEnvelope
        {
            Type = "browserStatus",
            TaskId = _taskId,
            BrowserId = browserId,
            Success = success,
            Message = message,
            Data = statusData
        }, cancellationToken);
    }

    private async Task SendAsync(PipeEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (_writer == null)
            throw new InvalidOperationException("未启动");

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

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        try { await WaitRunTasksAsync(TimeSpan.FromSeconds(3)); } catch { }

        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Dispose();
        _writeLock.Dispose();
        _cts.Dispose();

        await Task.CompletedTask;
    }

    private async Task WaitRunTasksAsync(TimeSpan timeout)
    {
        var tasks = new List<Task>();
        foreach (var task in _runTasks.Values)
        {
            tasks.Add(task);
        }

        if (tasks.Count == 0)
            return;

        try
        {
            var all = Task.WhenAll(tasks);
            var finished = await Task.WhenAny(all, Task.Delay(timeout));
            if (finished == all)
                await all;
        }
        catch
        {
        }
    }
}
