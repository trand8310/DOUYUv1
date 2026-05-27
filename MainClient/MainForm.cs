using MainClient.Common;
using MainClient.Infrastructure;
using MainClient.Ipc;
using MainClient.Logging;
using MainClient.LogViewer;
using MainClient.Models;
using MainClient.UiTask;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog.Events;
using System.Collections.Concurrent;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading.Channels;


namespace MainClient
{
    public partial class MainForm : Form
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly AppSettings _appSettings;
        private readonly AdeHelper _adeHelper;
        private readonly IpHelper _ipHelper;
        private readonly ProxyTester _ipTester;



        #region 任务调度
        private PipelineRunner<JsonNode>? _pipeline;
        private UiTaskRunner? _uiRunner;
        private AppAutoRestart? _appAutoRestart;
        private readonly AdTrafficAggregator _aggregator;

        #endregion

        #region LogWrite

        private readonly ConcurrentQueue<UiLogItem> _uiLogBuffer = new();
        private readonly System.Windows.Forms.Timer _uiTimer = new();
        private CancellationTokenSource _uiLogCts = new();
        private int _flushing = 0;
        private const int MaxFlushCount = 500;
        // 新控件
        private LogViewerUltra logViewer;
        private void StartLogConsumer()
        {
            // 初始化新控件
            logViewer = new LogViewerUltra()
            {
                Dock = DockStyle.Fill
            };
            groupBox4.Controls.Add(logViewer);

            // 后台读取日志
            Task.Run(async () =>
            {
                var reader = UiLogChannel.Channel.Reader;

                try
                {
                    await foreach (var item in reader.ReadAllAsync(_uiLogCts.Token))
                    {
                        if (_uiLogCts.IsCancellationRequested)
                            break;

                        _uiLogBuffer.Enqueue(item);
                    }
                }
                catch (OperationCanceledException) { }

            }, _uiLogCts.Token);

            // UI Timer
            _uiTimer.Interval = 200;
            _uiTimer.Tick += (_, __) =>
            {
                if (Interlocked.Exchange(ref _flushing, 1) == 1)
                    return;

                try
                {
                    FlushLogsToUi();
                }
                finally
                {
                    Interlocked.Exchange(ref _flushing, 0);
                }
            };
            _uiTimer.Start();

            this.FormClosing += (s, e) =>
            {
                try
                {
                    _uiTimer.Stop();
                    _uiLogCts.Cancel();
                    UiLogChannel.Channel.Writer.TryComplete();
                }
                catch { }
            };
        }
        private void FlushLogsToUi()
        {
            if (IsDisposed || Disposing)
                return;

            if (!IsHandleCreated || logViewer.IsDisposed)
                return;

            if (_uiLogBuffer.IsEmpty)
                return;

            int count = 0;

            while (_uiLogBuffer.TryDequeue(out var item))
            {
                logViewer.WriteLog(item.Message, ConvertLevel(item.Level));

                if (++count >= MaxFlushCount)
                    break;
            }
        }
        // 日志级别映射
        private LogLevel ConvertLevel(LogEventLevel level) => level switch
        {
            LogEventLevel.Verbose => LogLevel.Trace,
            LogEventLevel.Debug => LogLevel.Debug,
            LogEventLevel.Information => LogLevel.Information,
            LogEventLevel.Warning => LogLevel.Warning,
            LogEventLevel.Error => LogLevel.Error,
            _ => LogLevel.Information
        };

        public void LogWriteLine(string message)
        {
            _logger.LogInformation(message);
        }

        #endregion


        #region 应用设置
        private void LoadAppSetting()
        {
            textBox_ProxyIpUrl.Text = _appSettings.ProxyIpUrl;
            textBox_TaskApiUrl.Text = _appSettings.TaskApiUrl;
            textBox_DevApiUrl.Text = _appSettings.DevApiUrl;
            numericUpDown_TaskPullInterval.Value = _appSettings.TaskPullInterval;
            numericUpDown_UvExecutionInterval.Value = _appSettings.UvExecutionInterval;
            numericUpDown_MaxConcurrency.Value = _appSettings.MaxConcurrency;
            textBox_TaskName.Text = _appSettings.TaskName;
            numericUpDown_Multiple.Value = _appSettings.Multiple;
            numericUpDown_MainProcessResetIntervalMinutes.Value = _appSettings.MainProcessResetIntervalMinutes;
            checkBox_IsHiddenMode.Checked = _appSettings.IsHiddenMode;
            checkBox_IsProxyMode.Checked = _appSettings.IsProxyMode;
            numericUpDown_IpValidityDuration.Value = _appSettings.IpValidityDuration;
            checkBox_IsDetailLog.Checked = _appSettings.IsDetailLog;
            checkBox_IsRealIp.Checked = _appSettings.IsRealIp;
            checkBox_IsCheckIp.Checked = _appSettings.IsCheckIp;
        }
        private static object lock_config = new object();
        private void UpdateAppSetting()
        {
            lock (lock_config)
            {

                _appSettings.ProxyIpUrl = textBox_ProxyIpUrl.Text;
                _appSettings.TaskApiUrl = textBox_TaskApiUrl.Text;
                _appSettings.DevApiUrl = textBox_DevApiUrl.Text;
                _appSettings.TaskPullInterval = (int)numericUpDown_TaskPullInterval.Value;
                _appSettings.UvExecutionInterval = (int)numericUpDown_UvExecutionInterval.Value;
                _appSettings.MaxConcurrency = (int)numericUpDown_MaxConcurrency.Value;
                _appSettings.TaskName = textBox_TaskName.Text;
                _appSettings.Multiple = (int)numericUpDown_Multiple.Value;
                _appSettings.MainProcessResetIntervalMinutes = (int)numericUpDown_MainProcessResetIntervalMinutes.Value;
                _appSettings.IsHiddenMode = checkBox_IsHiddenMode.Checked;
                _appSettings.IsProxyMode = checkBox_IsProxyMode.Checked;
                _appSettings.IpValidityDuration = (int)numericUpDown_IpValidityDuration.Value;
                _appSettings.IsDetailLog = checkBox_IsDetailLog.Checked;
                _appSettings.IsRealIp = checkBox_IsRealIp.Checked;
                _appSettings.IsCheckIp = checkBox_IsCheckIp.Checked;

                UserConfigService.Save("AppSettings", _appSettings);
            }

        }
        #endregion



        public MainForm(
            AdeHelper adeHelper,
            IpHelper ipHelper,
            ProxyTester ipTester,
            AdTrafficAggregator aggregator,
            AppSettings appSettings,
            IHttpClientFactory httpClientFactory,
            ILogger<MainForm> logger)
        {
            InitializeComponent();
            this._adeHelper = adeHelper;
            this._ipHelper = ipHelper;
            this._ipTester = ipTester;
            this._aggregator = aggregator;
            this._appSettings = appSettings;
            this._logger = logger;
            this._httpClientFactory = httpClientFactory;

            LoadAppSetting();
            #region 数据初始化
            foreach (var item in new ManagementObjectSearcher("Select * from Win32_ComputerSystem").Get())
            {
                toolStripStatusLabel1.Text = $"CPU:{item["NumberOfLogicalProcessors"]}";
            }
            #endregion
        }


        private void MainForm_Load(object sender, EventArgs e)
        {
            StartLogConsumer();
            _logger.LogInformation("应用已启动");
            Task.Run(() =>
            {
                this.InvokeOnUiThreadIfRequired(() =>
                {

                    #region 控件初始化
                    var controls = new List<Control>() { groupBox2 };
                    foreach (var control in controls)
                    {
                        foreach (var c in control.Controls)
                        {
                            if (c is NumericUpDown)
                            {
                                (c as NumericUpDown).ValueChanged += (s, e) =>
                                {
                                    UpdateAppSetting();
                                };
                            }
                            else if (c is TextBox)
                            {
                                (c as TextBox).TextChanged += (s, e) =>
                                {
                                    UpdateAppSetting();
                                };
                            }
                            else if (c is CheckBox)
                            {
                                (c as CheckBox).Click += (s, e) =>
                                {
                                    UpdateAppSetting();
                                };
                            }
                            else if (c is RadioButton)
                            {
                                (c as RadioButton).Click += (s, e) =>
                                {
                                    UpdateAppSetting();
                                };
                            }
                            else if (c is ComboBox)
                            {
                                (c as ComboBox).SelectedIndexChanged += (s, e) =>
                                {
                                    UpdateAppSetting();
                                };
                            }
                        }
                    }
                    #endregion

                });
            });
        }



        /// <summary>
        /// 获取任务
        /// </summary>
        /// <param name="writer"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        private async Task ProducerAsync(ChannelWriter<JsonNode> writer, CancellationToken token)
        {
            Exception? completionError = null;

            try
            {
                var host = await CommonHelper.GetLocalHostAsync();
                while (!token.IsCancellationRequested)
                {
                    //$"{_appSettings.Value.TaskApiUrl}?type=1&action=getTask&task={_appSettings.Value.TaskName}&test=0&_t={System.DateTime.Now.Ticks}"
                    var url = $"{_appSettings.TaskApiUrl}?type=1&action=getTask&task={_appSettings.TaskName}&host={System.Web.HttpUtility.UrlEncode(host)}&ver={AppConsts.AppVersion}&test=0&_t={DateTime.Now.Ticks}";
                    var res = await _adeHelper.GetTaskAsync(url, token);
                    if (string.IsNullOrWhiteSpace(res))
                    {
                        LogWriteLine("读取任务异常");
                        await Task.Delay(_appSettings.TaskPullInterval, token);
                        continue;
                    }

                    JsonArray? data;
                    try
                    {
                        var json = JsonNode.Parse(res);
                        data = json["task"] as JsonArray;
                    }
                    catch (JsonException)
                    {
                        _logger.LogError("ProducerAsync json parse failed: {Response}", res);
                        await Task.Delay(_appSettings.TaskPullInterval, token);
                        continue;
                    }

                    if (data == null || data.Count == 0)
                    {
                        LogWriteLine("暂无任务");
                        await Task.Delay(_appSettings.TaskPullInterval, token);
                        continue;
                    }

                    int multiple = Math.Max(1, _appSettings.Multiple);
                    int totalEnqueued = 0;
                    for (int i = 0; i < multiple; i++)
                    {
                        foreach (var item in data)
                        {
                            if (!await writer.WaitToWriteAsync(token))
                                return;

                            await writer.WriteAsync(item?.DeepClone() ?? new JsonObject(), token);
                            totalEnqueued++;
                        }
                    }

                    LogWriteLine($"新增{totalEnqueued}条任务");
                    await Task.Delay(_appSettings.TaskPullInterval, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {

            }
            catch (Exception ex)
            {
                completionError = ex;
                throw;
            }
            finally
            {
                writer.TryComplete(completionError);
            }
        }

        /// <summary>
        /// 消费任务
        /// </summary>
        /// <param name="consumerId"></param>
        /// <param name="task"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        private async Task ConsumerAsync(int consumerId, JsonNode task, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();

                var parseResult = ParseTask(task);
                if (!parseResult.Success)
                {
                    _logger.LogWarning("ConsumerAsync skip malformed task: {Task}", task?.ToString());
                    return;
                }


                var ctx = parseResult.Context!;
                ApplyUvPvOverrides(ctx);

                var initDev = await GetDeviceForTaskAsync(ctx.OS, ctx.TaskId, 0, token);
                if (initDev == null)
                {
                    _logger.LogWarning("ConsumerAsync get device failed after retries. taskId={TaskId}, uv={Uv}", ctx.TaskId, 1);
                    return;
                }

                await PrepareProxyContextAsync(ctx, task, token);

                var ipTtlSeconds = _appSettings.IpValidityDuration;
                if (ipTtlSeconds <= 0)
                {
                    _logger.LogWarning("ConsumerAsync invalid IpTtl={IpTtl}, taskId={TaskId}", ipTtlSeconds, ctx.TaskId);
                    return;
                }

                bool stopRemainingUv = await ExecuteTaskByCefClientAsync(
                    ctx,
                    task,
                    consumerId,
                    initDev,
                    token);

                if (stopRemainingUv)
                {
                    _logger.LogInformation("ConsumerAsync stop remaining uv. taskId={TaskId}", ctx.TaskId);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (IOException ex) when (ex.Message.Contains("Pipe is broken", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("pipe has been ended", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(ex, "Pipe closed during shutdown. consumerId={consumerId}", consumerId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConsumerAsync failed:{Message}", ex.Message);
            }
        }




        private async Task<bool> ExecuteTaskByCefClientAsync(
           ConsumerTaskContext ctx,
           JsonNode rawTask,
           int consumerId,
           JsonNode initDev,
           CancellationToken token)
        {
            ctx.UniqueId = Guid.NewGuid().ToString("D");
            var cefProcessFileName = "CefClient.exe";
            var cefExePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "CefClient",
                cefProcessFileName);


            _logger.LogInformation(
                "Use {CefProcessFileName} for taskId={TaskId}, uniqueId={UniqueId}, consumer={ConsumerId}",
                cefProcessFileName,
                ctx.TaskId,
                ctx.UniqueId,
                consumerId);


            var cefConsumerId = consumerId.ToString();
            await using var session = new CefClientSession(cefExePath, TimeSpan.FromSeconds(15), cefConsumerId);

            session.OnLog += message =>
            {
                _logger.LogInformation("CefClient[{TaskId}] {Message}", ctx.TaskId, message);
                return Task.CompletedTask;
            };

            session.OnBrowserScreenshot += screenshot =>
            {
                if (!string.IsNullOrWhiteSpace(screenshot.TaskId) &&
                    !string.Equals(screenshot.TaskId, ctx.UniqueId, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.CompletedTask;
                }

                return Task.CompletedTask;
            };

            session.OnBrowserStatus += status =>
            {
                if (!string.IsNullOrWhiteSpace(status.TaskId) &&
                    !string.Equals(status.TaskId, ctx.UniqueId, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.CompletedTask;
                }

                var stage = status.Data?["stage"]?.GetValue<string>() ?? "unknown";
                var browserId = status.BrowserId ?? string.Empty;
                if (string.Equals(stage, "log", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "CefClient[{TaskId}][{BrowserId}] {Message}",
                        ctx.TaskId,
                        browserId,
                        status.Message);
                    return Task.CompletedTask;
                }

                _logger.LogInformation(
                    "CefClient browser status. taskId={TaskId}, browserId={BrowserId}, stage={Stage}, success={Success}, msg={Message}",
                    ctx.TaskId,
                    browserId,
                    stage,
                    status.Success,
                    status.Message);

                if (TryMapBrowserStatusToTaskState(stage, out var state))
                {
                    var count = status.Data?["count"]?.GetValue<int?>() ?? 1;
                    _aggregator.EnqueueTaskState(new AdTrafficTaskStateEvent(
                        ctx.TaskId,
                        state,
                        Math.Max(1, count),
                        status.Data?.ToJsonString()));
                }

                return Task.CompletedTask;
            };

            var completedUvTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int dispatchedUvCount = 0;
            int completedUvCount = 0;
            bool stopRemainingUvByResult = false;
            var inFlightBrowsers = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var uvRunTimeout = TimeSpan.FromSeconds(_appSettings.IpValidityDuration);

            void TryCompleteAll()
            {
                if (Volatile.Read(ref dispatchedUvCount) <= 0)
                    return;

                if (Volatile.Read(ref completedUvCount) >= Volatile.Read(ref dispatchedUvCount))
                    completedUvTcs.TrySetResult(true);
            }

            Task StartUvTimeoutWatchdogAsync(string browserId, CancellationToken watchdogToken)
            {
                return Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(uvRunTimeout, watchdogToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (!inFlightBrowsers.TryRemove(browserId, out _))
                        return;

                    var done = Interlocked.Increment(ref completedUvCount);
                    _logger.LogWarning(
                        "UV run timeout fallback. taskId={TaskId}, browserId={BrowserId}, timeout={TimeoutSeconds}s, completed={Completed}/{Dispatched}",
                        ctx.TaskId,
                        browserId,
                        (int)uvRunTimeout.TotalSeconds,
                        done,
                        Volatile.Read(ref dispatchedUvCount));

                    try
                    {
                        await session.RemoveBrowserAsync(ctx.UniqueId, browserId, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "Timeout fallback remove browser failed. taskId={TaskId}, browserId={BrowserId}",
                            ctx.TaskId,
                            browserId);
                    }

                    TryCompleteAll();
                }, CancellationToken.None);
            }

            session.OnBrowserResult += async response =>
            {
                if (!string.Equals(response.TaskId, ctx.UniqueId, StringComparison.OrdinalIgnoreCase))
                    return;

                if (string.IsNullOrWhiteSpace(response.BrowserId))
                    return;

                if (!inFlightBrowsers.TryRemove(response.BrowserId, out _))
                {
                    _logger.LogDebug(
                        "Ignore duplicated or late browserResult. taskId={TaskId}, browserId={BrowserId}",
                        ctx.TaskId,
                        response.BrowserId);
                    return;
                }

                var uvNumber = Interlocked.Increment(ref completedUvCount);
                _logger.LogInformation(
                    "RunBrowserAsync done. taskId={TaskId}, uv={Uv}, browserId={BrowserId}, success={Success}, msg={Message}",
                    ctx.TaskId,
                    uvNumber,
                    response.BrowserId,
                    response.Success,
                    response.Message);

                var result = new BrowserRunResponse
                {
                    Success = response.Success ?? false,
                    Message = response.Message ?? string.Empty,
                    Data = response.Data
                };

                if (ShouldStopRemainingUv(ctx, result))
                {
                    stopRemainingUvByResult = true;
                }

                var removedByCefClient = response.Data?["removedByCefClient"]?.GetValue<bool?>() ?? false;
                if (!removedByCefClient)
                {
                    try
                    {
                        await session.RemoveBrowserAsync(ctx.UniqueId, response.BrowserId, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "RemoveBrowserAsync failed after browserResult. taskId={TaskId}, browserId={BrowserId}",
                            ctx.TaskId, response.BrowserId);
                    }
                }

                if (Volatile.Read(ref completedUvCount) >= Volatile.Read(ref dispatchedUvCount))
                {
                    completedUvTcs.TrySetResult(true);
                }
            };

            try
            {
                await session.StartAsync(token);

                var startPayload = BuildStartPayload(ctx, rawTask);
                await session.StartTaskAsync(ctx.UniqueId, startPayload, token);

                var ipTtlSeconds = _appSettings.IpValidityDuration;
                using var ipTtlCts = new CancellationTokenSource(TimeSpan.FromSeconds(ipTtlSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, ipTtlCts.Token);
                var innerToken = linkedCts.Token;
                var uvIntervalMs = Math.Max(1000, _appSettings.UvExecutionInterval <= 0 ? 1000 : _appSettings.UvExecutionInterval);

                async Task<bool> WaitForDispatchedUvCompletionAsync()
                {
                    if (Volatile.Read(ref dispatchedUvCount) <= 0)
                        return false;

                    TryCompleteAll();

                    using var completionRegistration = innerToken.Register(() => completedUvTcs.TrySetCanceled(innerToken));
                    await completedUvTcs.Task;

                    return stopRemainingUvByResult;
                }



                for (int uvIndex = 0; uvIndex < ctx.TotalUV; uvIndex++)
                {
                    if (token.IsCancellationRequested)
                        return false;

                    string browserId = $"uv_{uvIndex + 1}";
                    _aggregator.EnqueueTaskState(new AdTrafficTaskStateEvent(ctx.TaskId, AdTrafficTaskStateKind.Request, 1));
                    try
                    {
                        var dev = uvIndex == 0 ? initDev : await GetDeviceForTaskAsync(ctx.OS, ctx.TaskId, uvIndex, innerToken);
                        if (dev == null)
                        {
                            _logger.LogWarning("GetDeviceForTaskAsync failed. taskId={TaskId}, uv={Uv}",
                                ctx.TaskId, uvIndex + 1);
                            continue;
                        }

                        NormalizeDevice(dev, ctx.OS);

                        if (!inFlightBrowsers.TryAdd(browserId, 0))
                        {
                            _logger.LogWarning(
                                "Duplicated in-flight browserId. taskId={TaskId}, browserId={BrowserId}",
                                ctx.TaskId,
                                browserId);
                            continue;
                        }

                        Interlocked.Increment(ref dispatchedUvCount);
                        _ = StartUvTimeoutWatchdogAsync(browserId, innerToken);

                        try
                        {
                            var uvPayload = BuildRunBrowserPayload(ctx, rawTask, dev, consumerId, uvIndex);
                            // 多 UV 只按配置间隔投递到 CefClient，不等待子进程完成 browserCreated。
                            // CefClient 的管道读取是顺序的，同一个 browserId 会先执行 createBrowser 再执行 runBrowser。
                            await session.CreateBrowserNoWaitAsync(ctx.UniqueId, browserId, uvPayload, innerToken);

                            await session.RunBrowserNoWaitAsync(
                                ctx.UniqueId,
                                browserId,
                                uvPayload,
                                innerToken);
                        }
                        catch
                        {
                            inFlightBrowsers.TryRemove(browserId, out _);
                            Interlocked.Decrement(ref dispatchedUvCount);
                            try
                            {
                                await session.RemoveBrowserAsync(ctx.UniqueId, browserId, CancellationToken.None);
                            }
                            catch
                            {
                            }
                            throw;
                        }

                        if (uvIndex < ctx.TotalUV - 1)
                            await Task.Delay(uvIntervalMs, innerToken);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return false;
                    }
                    catch (OperationCanceledException) when (ipTtlCts.IsCancellationRequested)
                    {
                        LogWriteLine($"任务 {ctx.TaskTitle}[{ctx.TaskId}] 的 IP 总有效时长已到，停止后续 UV。");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "ExecuteTaskByCefClientAsync uv failed. taskId={TaskId}, uv={Uv}, consumer={ConsumerId}",
                            ctx.TaskId, uvIndex + 1, consumerId);
                    }
                }

                return await WaitForDispatchedUvCompletionAsync();
            }
            finally
            {
                try
                {
                    await session.CloseGracefullyAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "CloseGracefullyAsync failed. taskId={TaskId}", ctx.TaskId);
                }
            }
        }



        private static bool TryMapBrowserStatusToTaskState(string? stage, out AdTrafficTaskStateKind state)
        {
            switch (stage?.Trim().ToLowerInvariant())
            {
                case "start":
                    state = AdTrafficTaskStateKind.Start;
                    return true;
                case "dsp":
                    state = AdTrafficTaskStateKind.DSP;
                    return true;
                case "click":
                    state = AdTrafficTaskStateKind.Clickthrough;
                    return true;
                case "success":
                    state = AdTrafficTaskStateKind.Success;
                    return true;
                case "error":
                    state = AdTrafficTaskStateKind.Error;
                    return true;
                case "failure":
                    state = AdTrafficTaskStateKind.Failure;
                    return true;
                case "complete":
                    state = AdTrafficTaskStateKind.Complete;
                    return true;
                case "x5sec":
                    state = AdTrafficTaskStateKind.X5Sec;
                    return true;
                default:
                    state = default;
                    return false;
            }
        }

        private JsonObject BuildStartPayload(ConsumerTaskContext ctx, JsonNode task)
        {
            return new JsonObject
            {
                ["taskId"] = ctx.UniqueId,
                ["taskTitle"] = ctx.TaskTitle ?? "",
                ["os"] = (int)(ctx.OS),
                ["totalUv"] = ctx.TotalUV,
                ["task"] = task.ToString()
            };
        }

        private JsonObject BuildRunBrowserPayload(
            ConsumerTaskContext ctx,
            JsonNode taskObj,
            JsonNode devObj,
            int consumerId,
            int uvIndex)
        {

            var timestamp = CommonHelper.UnixTimeNowSecond();

            var ua = devObj["ua"]?.GetValue<string>();
            var url = taskObj["url"]?.GetValue<string>();
            var referer = taskObj["referer"]?.GetValue<string>();

            //if (!string.IsNullOrWhiteSpace(referer))
            //    referer = UrlHelper.URLMacroReplacement(referer, ctx.RealIp, taskObj, devObj, ctx.OS, _appSettings, timestamp);

            //timestamp = CommonHelper.UnixTimeNowSecond();
            //if (!string.IsNullOrWhiteSpace(url))
            //    url = UrlHelper.URLMacroReplacement(url, ctx.RealIp, taskObj, devObj, ctx.OS, _appSettings, timestamp);


            return new JsonObject
            {
                ["taskId"] = ctx.UniqueId,
                ["taskTitle"] = ctx.TaskTitle ?? "",
                ["uvIndex"] = uvIndex,
                ["consumerId"] = consumerId,
                ["os"] = (int)(ctx.OS),
                ["device"] = devObj.DeepClone(),
                ["userAgent"] = ua,
                ["isProxyMode"] = _appSettings.IsProxyMode,
                ["proxy_server"] = ctx.ProxyServer ?? string.Empty,
                ["task"] = taskObj.DeepClone(),
                ["url"] = url,
                ["referer"] = referer,
                // OSR 端用这些短超时防止慢页面长期占住本次 UV，影响后续任务调度。
                ["loadTimeoutMs"] = 8000,
                ["firstScreenshotDelayMs"] = 1000,
                ["finalScreenshotDelayMs"] = 1500,
                ["screenshotTimeoutMs"] = 3000,
                ["titleTimeoutMs"] = 1000,
            };
        }

        private bool ShouldStopRemainingUv(ConsumerTaskContext ctx, BrowserRunResponse result)
        {
            // 这里你先按你自己的业务判断
            // 例如 result.Data 里回了 stopRemainingUv = true
            var stop = result.Data?["stopRemainingUv"]?.GetValue<bool?>() ?? false;
            return stop;
        }


        /// <summary>
        /// 解析任务
        /// </summary>
        /// <param name="task"></param>
        /// <returns></returns>
        private ParseTaskResult ParseTask(JsonNode task)
        {
            if (task is not JsonObject taskObj)
                return new ParseTaskResult { Success = false };

            var taskIdToken = taskObj["id"];
            var url = taskObj["url"]?.GetValue<string>();
            var referer = GetFirstString(taskObj["referer"]);
            var totalUvToken = taskObj["uv"];
            var totalPvToken = taskObj["pv"];

            if (taskIdToken == null || totalUvToken == null || totalPvToken == null || string.IsNullOrWhiteSpace(url))
                return new ParseTaskResult { Success = false };

            var devClientId = taskObj["client"]?.GetValue<string>()?
                .Split(new[] { "|" }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "0";

            var ctx = new ConsumerTaskContext
            {
                TaskId = taskIdToken.GetValue<int>(),
                TotalUV = Math.Max(1, totalUvToken.GetValue<int>()),
                TotalPV = Math.Max(1, totalPvToken.GetValue<int>()),
                DevClientId = devClientId,
                OS = _adeHelper.GetOS(devClientId),
                TaskTitle = taskObj["title"]?.GetValue<string>() ?? string.Empty,
                StartTime = DateTime.Now
            };

            return new ParseTaskResult
            {
                Success = true,
                Context = ctx
            };
        }

        private static string GetFirstString(JsonNode? node)
        {
            if (node == null)
                return string.Empty;

            try
            {
                if (node is JsonArray array)
                {
                    return array.FirstOrDefault()?.GetValue<string>() ?? string.Empty;
                }

                return node.GetValue<string>() ?? string.Empty;
            }
            catch
            {
                return node.ToString();
            }
        }

        /// <summary>
        /// 应用 UV / PV 覆盖配置
        /// </summary>
        /// <param name="ctx"></param>
        private void ApplyUvPvOverrides(ConsumerTaskContext ctx)
        {

        }

        #region 代理 / IP 信息
        /// <summary>
        /// 准备代理 / IP 信息
        /// </summary>
        /// <param name="ctx"></param>
        /// <param name="task"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        private async Task PrepareProxyContextAsync(ConsumerTaskContext ctx, JsonNode task, CancellationToken token)
        {
            ctx.ProxyServer = null;
            ctx.RealIp = string.Empty;
            ctx.IpInfo = null;

            if (_appSettings.IsProxyMode)
            {
                if (!string.IsNullOrWhiteSpace(_appSettings.ProxyIpUrl))
                {
                    await PrepareRemoteProxyAsync(ctx, task, token);
                }
                else
                {
                    await PrepareLocalProxyAsync(ctx, token);
                }
            }
            else
            {
                await PrepareDirectNetworkIpInfoAsync(ctx, token);
            }
        }
        /// <summary>
        /// 远程代理模式
        /// </summary>
        /// <param name="ctx"></param>
        /// <param name="task"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        private async Task PrepareRemoteProxyAsync(ConsumerTaskContext ctx, JsonNode task, CancellationToken token)
        {
            const int maxRetry = 10;

            for (int retry = 1; retry <= maxRetry; retry++)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    _aggregator.EnqueueFetchedIp(ctx.TaskId, 1);

                    var ipEntity = await _ipHelper.GetProxyIpAsync(task);
                    if (ipEntity == null)
                    {
                        LogWriteLine("获取IP错误");
                        await Task.Delay(Random.Shared.Next(100, 200), token);
                        continue;
                    }

                    FillProxyServerFromEntity(ctx, ipEntity);

                    if (string.IsNullOrWhiteSpace(ctx.ProxyServer) || !IsValidProxyServer(ctx.ProxyServer))
                    {
                        LogWriteLine($"IP异常,{ctx.ProxyServer}");
                        await Task.Delay(Random.Shared.Next(100, 200), token);
                        continue;
                    }

                    if (_appSettings.IsCheckIp || _appSettings.IsRealIp)
                    {
                        var ok = await TryFillIpInfoAsync(ctx, token);
                        if (!ok)
                        {
                            LogWriteLine($"无法获取IP信息,{ctx.ProxyServer}");
                            await Task.Delay(Random.Shared.Next(100, 200), token);
                            continue;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(ctx.RealIp))
                    {
                        _aggregator.EnqueueConsumedIp(ctx.TaskId, ctx.RealIp, 1);
                    }

                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogWriteLine($"IP异常,{ex.Message}");

                    if (ex.Message.Contains("没有满足您选择的条件IP"))
                        await Task.Delay(Random.Shared.Next(2000, 3000), token);

                    await Task.Delay(Random.Shared.Next(300, 500), token);
                }
            }

            throw new InvalidOperationException($"获取代理 IP 失败，taskId={ctx.TaskId}");
        }
        /// <summary>
        /// 本地代理模式
        /// </summary>
        /// <param name="ctx"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        private async Task PrepareLocalProxyAsync(ConsumerTaskContext ctx, CancellationToken token)
        {
            ctx.ProxyServer = "127.0.0.1:7890";

            var result = await _ipTester.TestAsync(ctx.ProxyServer);
            if (!result.IsValid)
            {
                LogWriteLine($"无法获取IP信息,{ctx.ProxyServer}");
                throw new InvalidOperationException($"无法获取IP信息,{ctx.ProxyServer}");
            }

            ApplyIpTestResult(ctx, result);
        }
        /// <summary>
        /// 非代理模式
        /// </summary>
        /// <param name="ctx"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        private async Task PrepareDirectNetworkIpInfoAsync(ConsumerTaskContext ctx, CancellationToken token)
        {
            if (!_appSettings.IsCheckIp && !_appSettings.IsRealIp)
                return;

            var result = await _ipTester.TestAsync(ctx.ProxyServer);
            if (!result.IsValid)
            {
                LogWriteLine($"无法获取IP信息,{ctx.ProxyServer}");
                throw new InvalidOperationException($"无法获取IP信息,{ctx.ProxyServer}");
            }

            ApplyIpTestResult(ctx, result);
        }
        #endregion

        #region 辅助方法：填代理 / 验证代理 / 填 IP 结果
        /// <summary>
        /// 辅助方法：填代理 / 验证代理 / 填 IP 结果
        /// </summary>
        /// <param name="ctx"></param>
        /// <param name="ipEntity"></param>
        private void FillProxyServerFromEntity(ConsumerTaskContext ctx, dynamic ipEntity)
        {
            if (ipEntity.format == IPFormat.JSON)
            {
                ctx.ProxyServer = $"{ipEntity.json["ip"]}:{ipEntity.json["port"]}";

                if (_appSettings.IsRealIp)
                {
                    ctx.RealIp =
                        ipEntity.json["rip"]?.GetValue<string>() ??
                        ipEntity.json["real_ip"]?.GetValue<string>() ??
                        ipEntity.json["realIp"]?.GetValue<string>() ??
                        string.Empty;
                }
            }
            else
            {
                ctx.ProxyServer = ipEntity.value;
                if (_appSettings.IsRealIp)
                    ctx.RealIp = ctx.ProxyServer ?? string.Empty;
            }
        }

        /// <summary>
        /// 验证代理1
        /// </summary>
        /// <param name="proxyServer"></param>
        /// <returns></returns>
        private bool IsValidProxyServer(string proxyServer)
        {
            const string pattern = @"(?:(?:[0,1]?\d?\d|2[0-4]\d|25[0-5])\.){3}(?:[0,1]?\d?\d|2[0-4]\d|25[0-5]):\d{1,5}";
            return Regex.IsMatch(proxyServer, pattern);
        }

        /// <summary>
        /// 验证代理2
        /// </summary>
        /// <param name="ctx"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        private async Task<bool> TryFillIpInfoAsync(ConsumerTaskContext ctx, CancellationToken token)
        {
            var result = await _ipTester.TestAsync(ctx.ProxyServer);
            if (!result.IsValid)
                return false;

            ApplyIpTestResult(ctx, result);
            return true;
        }
        /// <summary>
        /// 验证代理3
        /// </summary>
        /// <param name="ctx"></param>
        /// <param name="result"></param>

        private void ApplyIpTestResult(ConsumerTaskContext ctx, dynamic result)
        {
            if (result.SuccessUrl.Equals("http://ip-api.com/json") ||
                result.SuccessUrl.Equals("http://117.21.200.221/api/dash/ipinfo.php") ||
                result.SuccessUrl.Equals("http://117.21.200.18:9000/api/dash/ipinfo.php") ||
                result.SuccessUrl.Equals("http://211.154.24.179:9000/api/dash/ipinfo.php"))
            {
                ctx.IpInfo = JsonNode.Parse(result.Data)?.AsObject();
                ctx.RealIp = ctx.IpInfo["query"]?.GetValue<string>() ?? string.Empty;
            }
            else
            {
                var ipJson = JsonNode.Parse(result.Data)?.AsObject();
                if (ipJson?.ContainsKey("query") == true)
                    ctx.RealIp = ipJson["query"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(ctx.RealIp) && ipJson?.ContainsKey("ip") == true)
                    ctx.RealIp = ipJson["ip"]?.GetValue<string>() ?? string.Empty;

                ctx.IpInfo = new JsonObject
                {
                    ["query"] = ctx.RealIp
                };
            }
        }
        #endregion

        /// <summary>
        /// 获取设备
        /// </summary>
        /// <param name="os"></param>
        /// <param name="taskId"></param>
        /// <param name="uvIndex"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        private async Task<JsonNode?> GetDeviceForTaskAsync(OSType os, int taskId, int uvIndex, CancellationToken token)
        {
            for (int retry = 0; retry < 5; retry++)
            {
                token.ThrowIfCancellationRequested();

                var dev = await _adeHelper.GetDeviceAsync(os, 100);
                if (dev != null)
                    return dev;
            }

            _logger.LogWarning(
                "ConsumerAsync get device failed after retries. taskId={TaskId}, uv={Uv}",
                taskId, uvIndex + 1);

            return null;
        }

        /// <summary>
        /// 标准化设备信息
        /// </summary>
        /// <param name="dev"></param>
        /// <param name="os"></param>
        private void NormalizeDevice(JsonNode dev, OSType os)
        {
            var ua = dev["ua"]?.GetValue<string>() ?? string.Empty;

            if (os == OSType.ANDROID)
            {

            }
            else if (os == OSType.IOS)
            {
                dev["full_version"] = dev["osv"]?.DeepClone();
            }
            else if (os == OSType.PC)
            {
                dev["gpu"] = dev["renderer"]?.DeepClone();
                dev["vendor"] = dev["vender"]?.DeepClone();

            }
        }

        /// <summary>
        /// 构造插件参数
        /// </summary>
        /// <param name="ctx"></param>
        /// <param name="task"></param>
        /// <param name="dev"></param>
        /// <param name="consumerId"></param>
        /// <param name="uvIndex"></param>
        /// <returns></returns>
        private JsonObject BuildPluginArgs(ConsumerTaskContext ctx, JsonNode task, JsonNode dev, int consumerId, int uvIndex)
        {
            var cacheName = $"s{consumerId}_{uvIndex + 1}";

            var args = new JsonObject
            {
                ["task"] = task.DeepClone(),
                ["dev"] = dev.DeepClone(),
                ["ipInfo"] = ctx.IpInfo?.DeepClone(),
                ["isProxyMode"] = _appSettings.IsProxyMode,
                ["proxy_server"] = ctx.ProxyServer,
                ["realIp"] = ctx.RealIp,
                ["isHiddenMode"] = _appSettings.IsHiddenMode,
                ["cacheName"] = cacheName,
                ["processIndex"] = consumerId,
                ["totalPV"] = ctx.TotalPV,
                ["currentUV"] = uvIndex + 1,
                ["os"] = (int)ctx.OS,
                ["isTest"] = _appSettings.IsTest,
            };

            return args;
        }


        private void InitPipelineRunner()
        {
            int capacity = Math.Max(1, _appSettings.Multiple * _appSettings.MaxConcurrency);
            int consumerCount = Math.Max(1, _appSettings.MaxConcurrency);
            _pipeline = new PipelineRunner<JsonNode>(
                capacity,
                consumerCount,
                ProducerAsync,
                ConsumerAsync
            );
            _pipeline.ProgressChanged += _ =>
            {
                if (IsDisposed || Disposing)
                    return;
            };
            _pipeline.Started += () =>
            {
                this.InvokeOnUiThreadIfRequired(() =>
                {
                    lblStatus.Text = "任务状态：Running";
                });
            };
            _pipeline.Completed += () =>
            {
                this.InvokeOnUiThreadIfRequired(() =>
                {
                    lblStatus.Text = "任务状态：Completed";
                });
            };
            _pipeline.Canceled += () =>
            {
                this.InvokeOnUiThreadIfRequired(() =>
                {
                    lblStatus.Text = "任务状态：Canceled";
                });
            };
            _pipeline.Faulted += ex => _logger.LogError(ex, "Pipeline faulted");
        }

        private async Task StartRunnerAsync()
        {
            //string version = comboBox_KernelVersion.Text;
            //var chromeDir = Path.Combine(
            //    AppDomain.CurrentDomain.BaseDirectory,
            //    "File", "chrome-win", version, version);

            //if (!Directory.Exists(chromeDir))
            //{
            //    await DownloadBrowserAsync(version);
            //    if (!Directory.Exists(chromeDir))
            //    {
            //        _logger.LogWarning("Chrome kernel missing after download: {ChromeDir}", chromeDir);
            //        MessageBox.Show("浏览器内核缺失，请检查下载配置后重试。");
            //        return;
            //    }
            //}

            //if (_appSettings.UseLocalWord)
            //{
            //    var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", $"{_appSettings.WordName}.txt");
            //    if (!File.Exists(filePath))
            //    {
            //        await _adeHelper.DownloadWordFileByNameAsync(_appSettings.WordName);
            //    }
            //    if (!File.Exists(filePath))
            //    {
            //        _logger.LogWarning("缺少本地词库: {filePath}", filePath);
            //        return;
            //    }
            //}


            await _aggregator.StartAsync();


            InitPipelineRunner();

            var runner = new UiTaskRunner(token => _pipeline!.RunAsync(token));

            ConfigureRunner(runner);

            _uiRunner = runner;
            _uiRunner.Start();


            _appAutoRestart?.Dispose();
            _appAutoRestart = null;
            var restartInterval = CommonHelper.GetRandomizedInterval(_appSettings.MainProcessResetIntervalMinutes, 180);
            _appAutoRestart = new AppAutoRestart(
                restartInterval,
                () =>
                {
                    return _uiRunner != null && _uiRunner.State == RunnerState.Running;
                });

            _appAutoRestart.Start();
        }
        private async Task StopRunnerAsync()
        {
            try
            {
                _appAutoRestart?.Stop();

                if (_uiRunner != null)
                {
                    await _uiRunner.StopAsync();
                }
                await _aggregator.StopAsync();
            }
            finally
            {
                _appAutoRestart = null;
            }
        }
        private void ConfigureRunner(UiTaskRunner runner)
        {
            int clearTick = 0;

            runner.StateChanged += state =>
            {
                this.InvokeOnUiThreadIfRequired(() =>
                {
                    lblStatus.Text = $"任务状态：{state}";
                    btnStartStop.Text = state == RunnerState.Running ? "停止" : "开始";
                });
            };

            runner.Faulted += ex =>
            {
                _logger.LogError(ex, "UiTaskRunner faulted");
            };

            runner.LogEmitted += log =>
            {
                if (_appSettings.IsDetailLog)
                {
                    if (log.Exception == null)
                        _logger.LogInformation("[{Source}] {Message}", log.Source, log.Message);
                    else
                        _logger.LogWarning(log.Exception, "[{Source}] {Message}", log.Source, log.Message);
                }
            };

            // 1秒一次：UI统计刷新
            runner.SetPeriodicAction(
                interval: TimeSpan.FromSeconds(1),
                onTick: async token =>
                {
                    var elapsed = runner.RunElapsed;
                    var totalStats = _aggregator.GetHostTaskStats();
                    this.InvokeOnUiThreadIfRequired(() =>
                    {
                        //label_request.Text = $"提交数量:{totalStats.Request}";
                        //label_start.Text = $"执行数量:{totalStats.Start}";
                        //label_dsp.Text = $"曝光数量:{totalStats.DSP}";

                        //label5.Text = $"提交数量:{totalStats.Request}";
                        //label6.Text = $"执行数量:{totalStats.Start}";
                        //label7.Text = $"曝光数量:{totalStats.DSP}";
                        //label8.Text = $"点击数量:{totalStats.Clickthrough}";
                        //label9.Text = $"成功数量:{totalStats.Success}";
                        //toolStripStatusLabel4.Text = $"执行总量：{QTPTotalStartCount + totalStats.Start}";
                        //toolStripStatusLabel5.Text = $"曝光总量：{QTPTotalDspCount + totalStats.DSP}";
                        //toolStripStatusLabel6.Text = $"点击总量：{QTPTotalClickthroughCount + totalStats.Clickthrough}";
                        label7.Text = $"运行时长:{elapsed:hh\\:mm\\:ss}";
                    });

                    await Task.CompletedTask;
                },
                name: "RefreshStatsUi",
                skipIfRunning: true,
                timeout: TimeSpan.FromSeconds(2),
                circuitBreakThreshold: 10,
                circuitBreakCooldown: TimeSpan.FromSeconds(30)
            );
        }


        private async void btnStartStop_Click(object sender, EventArgs e)
        {
            if (!btnStartStop.Enabled)
                return;

            btnStartStop.Enabled = false;

            try
            {
                if (_uiRunner != null && _uiRunner.State is RunnerState.Running or RunnerState.Stopping)
                {
                    await StopRunnerAsync();
                }
                else
                {
                    await StartRunnerAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "btnStartStop_Click failed");
                MessageBox.Show($"启动/停止任务失败: {ex.Message}");
            }
            finally
            {
                this.InvokeOnUiThreadIfRequired(() =>
                {
                    btnStartStop.Enabled = true;
                });

            }
        }
    }

}
