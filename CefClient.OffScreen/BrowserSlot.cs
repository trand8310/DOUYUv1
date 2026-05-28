namespace CefClient
{
    using CefClient.Common;
    using CefClient.Handler;
    using CefSharp;
    using CefSharp.DevTools.Emulation;
    using CefSharp.OffScreen;
    using System;
    using System.Collections.Specialized;
    using System.Diagnostics;
    using System.Globalization;
    using System.Net;
    using System.Text.Json.Nodes;

    public sealed class BrowserSlot : IAsyncDisposable
    {
        private const int DefaultLoadTimeoutMs = 8000;
        private const int DefaultScreenshotTimeoutMs = 3000;
        private const int DefaultTitleTimeoutMs = 1000;
        private const int DefaultInitialLoadTimeoutMs = 5000;

        public string BrowserId { get; }
        private readonly Action<string, byte[]> _screenshotReady;
        private readonly Action<string> _log;
        private readonly Func<BrowserRunStatus, CancellationToken, Task>? _statusChanged;
        private int _disposed;

        public BrowserSlot(
            string browserId,
            Action<string, byte[]> screenshotReady,
            Action<string> log,
            Func<BrowserRunStatus, CancellationToken, Task>? statusChanged = null)
        {
            BrowserId = browserId;
            _screenshotReady = screenshotReady;
            _log = log;
            _statusChanged = statusChanged;
        }

        private async Task<string> GetPageTitleAsync(ChromiumWebBrowser browser, int timeoutMs, CancellationToken cancellationToken)
        {
            if (browser == null || browser.IsDisposed)
                return "";

            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (browser.IsDisposed)
                    return "";

                if (browser.CanExecuteJavascriptInMainFrame)
                {
                    try
                    {
                        var result = await browser.EvaluateScriptAsync("document.title")
                            .WaitAsync(TimeSpan.FromMilliseconds(Math.Min(500, timeoutMs)), cancellationToken);
                        return result.Success ? result.Result?.ToString() ?? "" : "";
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // 继续等一下再试，但总等待时间受 timeoutMs 限制。
                    }
                }

                await Task.Delay(100, cancellationToken);
            }

            return "";
        }

        public async Task<BrowserRunResult> RunAsync(JsonNode? payload, CancellationToken cancellationToken = default)
        {

            var task = payload?["task"];
            var sleepDelayMs = GetSleepDelayMilliseconds(task);
            var url = payload?["url"]?.ToString();
            var referer = GetString(payload, "referer");
            if (string.IsNullOrWhiteSpace(referer))
                referer = GetString(task, "referer");
            var taskId = payload?["taskId"]?.ToString() ?? BrowserId;
            var consumerId = payload?["consumerId"]?.ToString() ?? "unknown";
            var uvIndex = payload?["uvIndex"]?.ToString() ?? BrowserId;
            var isHiddenMode = payload?["isHiddenMode"]?.GetValue<bool>() ?? true;

            try
            {
                await PublishStatusAsync("start", true, "browser created", cancellationToken);
                if (string.IsNullOrWhiteSpace(url))
                {
                    await PublishStatusAsync("error", false, "url 不能为空", cancellationToken, new JsonObject
                    {
                        ["taskId"] = taskId,
                        ["consumerId"] = consumerId,
                        ["uvIndex"] = uvIndex
                    });

                    return new BrowserRunResult
                    {
                        BrowserId = BrowserId,
                        Success = false,
                        Message = "url 不能为空"
                    };
                }
                var cachePath = Path.Combine(CefCachePaths.RootCachePath, $"s{uvIndex}");


                var os = GetNullableInt(payload, "os") ?? 0;
                var device = payload?["device"];
                var sw = GetNullableInt(device, "sw") ?? 1080;
                var sh = GetNullableInt(device, "sh") ?? 1920;
                var ua = device?["ua"]?.ToString();
                var platform = os == 1 ? "Android" : "iPhone";
                var devProfile = DeviceViewportMatcher.Match(sw, sh, os == 2 ? DeviceSystemType.IOS : DeviceSystemType.Android);

                var browserSettings = new BrowserSettings
                {
                    WindowlessFrameRate = 10,
                    Javascript = CefState.Enabled,
                    ImageLoading = CefState.Enabled,
                    WebGl = CefState.Disabled,
                };
                var requestContextSettings = new RequestContextSettings
                {
                    PersistSessionCookies = false,
                    CachePath = cachePath,
                    AcceptLanguageList = "zh-CN,zh;q=0.9",
                };
                using var requestContext = new RequestContext(requestContextSettings);
                using var browser = new ChromiumWebBrowser("about:blank", browserSettings, requestContext)
                {
                    Size = new Size(sw, sh),
                    RequestHandler = new ExternalProtocolRequestHandler(message => _log($"{BrowserId}: {message}"))
                };
                await browser.WaitForInitialLoadAsync().WaitAsync(TimeSpan.FromMilliseconds(DefaultInitialLoadTimeoutMs), cancellationToken);
                await ConfigureProxyAsync(requestContext, payload, cancellationToken);
                using var devToolsClient = browser.GetDevToolsClient();
                if (GetBool(payload, "clearStorage", false))
                {
                    await devToolsClient.Storage.ClearDataForOriginAsync("*", "cache_storage,cookies,local_storage");
                }
                await devToolsClient.Emulation.SetUserAgentOverrideAsync(
                    userAgent: ua,
                    platform: (os == 2 ? "Linux aarch64" : "iOS"),
                    userAgentMetadata: new CefSharp.DevTools.Emulation.UserAgentMetadata()
                    {
                        Mobile = (os == 1 || os == 2),
                        Platform = platform,
                        PlatformVersion = device?["osv"]?.ToString() ?? "",
                        Bitness = "",
                        Architecture = "",
                        Model = device?["model"]?.ToString() ?? "",
                        Brands = new List<UserAgentBrandVersion>(),
                        FullVersionList = new List<UserAgentBrandVersion>(),
                    }
                 );
                await devToolsClient.Emulation.SetDeviceMetricsOverrideAsync(
                    width: devProfile.ViewportWidth,
                    height: devProfile.ViewportHeight,
                    deviceScaleFactor: devProfile.DeviceScaleFactor,
                    mobile: true,
                    scale: 1.0
                    );
                await devToolsClient.Emulation.SetTouchEmulationEnabledAsync(true, Random.Shared.Next(4, 6));
                await devToolsClient.Emulation.SetScrollbarsHiddenAsync(true);



                var refererHeaders = BuildRefererHeaders(referer);



                var loadTimeoutMs = GetPositiveInt(payload, "loadTimeoutMs", DefaultLoadTimeoutMs);
                var screenshotTimeoutMs = GetPositiveInt(payload, "screenshotTimeoutMs", DefaultScreenshotTimeoutMs);
                var titleTimeoutMs = GetPositiveInt(payload, "titleTimeoutMs", DefaultTitleTimeoutMs);
                var pvIntervalMs = 1000;
                var pvTotal = GetPositiveInt(task, "pv", 1);
                WaitForNavigationAsyncResponse? lastLoadResponse = null;
                var lastLoadTimedOut = false;
                var completedPv = 0;
                // OSR 模式每次 runBrowser 都是一次性浏览器；同一个 RunAsync 内的 pv 循环复用同一个浏览器上下文。
                for (var pvIndex = 1; pvIndex <= pvTotal; pvIndex++)
                {
                    var navigationTask = browser.WaitForNavigationAsync(
                        TimeSpan.FromMilliseconds(loadTimeoutMs),
                        cancellationToken);

                    if (!string.IsNullOrWhiteSpace(referer))
                    {
                        LoadUrl(browser, url, "GET", referer);
                    }
                    else
                    {
                        browser.Load(url);
                    }
                    WaitForNavigationAsyncResponse? loadResponse = null;
                    var loadTimedOut = false;
                    try
                    {
                        loadResponse = await navigationTask;
                    }
                    catch (TimeoutException)
                    {
                        loadTimedOut = true;
                        await PublishLogAsync($"PV {pvIndex}/{pvTotal} navigation timeout after {loadTimeoutMs}ms. url={url}");
                        TryStopBrowser(browser);
                    }
                    var loadFailed = loadResponse != null && loadResponse.ErrorCode != CefErrorCode.None;
                    var loadCompleted = loadResponse != null && !loadTimedOut && loadResponse.ErrorCode == CefErrorCode.None;
                    lastLoadResponse = loadResponse;
                    lastLoadTimedOut = loadTimedOut;
                    completedPv = pvIndex;
                    var pvData = new JsonObject
                    {
                        ["url"] = url,
                        ["referer"] = referer,
                        ["cachePath"] = cachePath,
                        ["pvIndex"] = pvIndex,
                        ["pvTotal"] = pvTotal,
                        ["pvIntervalMs"] = pvIntervalMs,
                        ["loadCompleted"] = loadCompleted,
                        ["loadTimedOut"] = loadTimedOut,
                        ["loadErrorCode"] = loadResponse?.ErrorCode.ToString() ?? string.Empty,
                        ["httpStatusCode"] = loadResponse?.HttpStatusCode ?? -1,
                        ["loadTimeoutMs"] = loadTimeoutMs
                    };
                    if (loadFailed)
                    {
                        await PublishLogAsync($"PV {pvIndex}/{pvTotal} load failed. error={loadResponse?.ErrorCode}, httpStatus={loadResponse?.HttpStatusCode}");
                        await PublishStatusAsync("error", false, $"第 {pvIndex}/{pvTotal} 次 PV 页面加载失败", cancellationToken, pvData);
                        return new BrowserRunResult
                        {
                            BrowserId = BrowserId,
                            Success = false,
                            Message = "页面加载失败",
                            Data = pvData
                        };
                    }
                    //await PublishLogAsync($"PV {pvIndex}/{pvTotal} completed. loadCompleted={loadCompleted}, timedOut={loadTimedOut}, httpStatus={loadResponse?.HttpStatusCode ?? -1}");
                    //await PublishStatusAsync("pv", true, loadCompleted ? $"pv {pvIndex}/{pvTotal} opened" : $"pv {pvIndex}/{pvTotal} 页面加载较慢，已按超时继续", cancellationToken, pvData);
                    if (pvIndex < pvTotal && pvIntervalMs > 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(pvIntervalMs), cancellationToken);
                    }
                }
                var finalLoadCompleted = lastLoadResponse != null && !lastLoadTimedOut && lastLoadResponse.ErrorCode == CefErrorCode.None;
                await PublishStatusAsync("dsp", true, finalLoadCompleted ? "page opened" : "页面加载较慢，已按超时继续", cancellationToken);

                if (!isHiddenMode)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(screenshotTimeoutMs), cancellationToken);
                    var screenshotShown = await TryCaptureAndShowScreenshotAsync(browser, screenshotTimeoutMs, cancellationToken);
                    var title = await GetPageTitleAsync(browser, titleTimeoutMs, cancellationToken);
                    await PublishLogAsync($"Final screenshot captured={screenshotShown}, title={title}");
                }
                var successData = new JsonObject
                {
                    ["url"] = url,
                    ["referer"] = referer,
                    ["pvTotal"] = pvTotal,
                    ["completedPv"] = completedPv,
                    ["pvIntervalMs"] = pvIntervalMs,
                    ["loadCompleted"] = finalLoadCompleted,
                    ["loadTimedOut"] = lastLoadTimedOut,
                    ["loadErrorCode"] = lastLoadResponse?.ErrorCode.ToString() ?? string.Empty,
                    ["httpStatusCode"] = lastLoadResponse?.HttpStatusCode ?? -1,
                    ["loadTimeoutMs"] = loadTimeoutMs,
                    ["cachePath"] = cachePath,
                    ["osrOneShot"] = true,
                    ["disposedByRunAsync"] = true
                };
               
                //await PublishLogAsync($"RunAsync success. completedPv={completedPv}/{pvTotal}, finalLoadCompleted={finalLoadCompleted}");
                // 当前 RunAsync 暂未执行点击动作；后续如果在这里补充点击流程，点击完成后调用 PublishStatusAsync("click", ...)。
                await PublishStatusAsync("click", true, finalLoadCompleted ? "page opened" : "页面加载较慢，已按超时继续", cancellationToken);

                await PublishStatusAsync("success", true, "执行成功", cancellationToken);

                return new BrowserRunResult
                {
                    BrowserId = BrowserId,
                    Success = true,
                    Message = finalLoadCompleted ? $"执行成功" : "页面加载较慢，已按超时继续",
                    Data = successData
                };
            }
            catch (OperationCanceledException)
            {
                await PublishLogAsync("RunAsync canceled");

                await PublishStatusAsync("error", false, "取消", CancellationToken.None, new JsonObject
                {
                    ["url"] = url ?? string.Empty,
                    ["referer"] = referer,
                    ["taskId"] = taskId,
                    ["consumerId"] = consumerId,
                    ["uvIndex"] = uvIndex
                });

                return new BrowserRunResult
                {
                    BrowserId = BrowserId,
                    Success = false,
                    Message = "取消"
                };
            }
            catch (Exception ex)
            {
                await PublishLogAsync($"RunAsync exception: {ex.Message}");

                await PublishStatusAsync("error", false, ex.Message, CancellationToken.None, new JsonObject
                {
                    ["url"] = url ?? string.Empty,
                    ["referer"] = referer,
                    ["taskId"] = taskId,
                    ["consumerId"] = consumerId,
                    ["uvIndex"] = uvIndex
                });

                return new BrowserRunResult
                {
                    BrowserId = BrowserId,
                    Success = false,
                    Message = ex.Message
                };
            }
            finally
            {
                if (sleepDelayMs > 0)
                {
                    await Task.Delay(sleepDelayMs, CancellationToken.None);
                }

                await PublishStatusAsync("complete", true, "RunAsync complete", CancellationToken.None, new JsonObject
                {
                    ["url"] = url ?? string.Empty,
                    ["referer"] = referer,
                    ["taskId"] = taskId,
                    ["consumerId"] = consumerId,
                    ["uvIndex"] = uvIndex,
                    ["sleepDelayMs"] = sleepDelayMs
                });
            }
        }






        private static WebHeaderCollection? BuildRefererHeaders(string? referer)
        {
            if (string.IsNullOrWhiteSpace(referer))
                return null;

            return new WebHeaderCollection
            {
                [HttpRequestHeader.Referer] = referer
            };
        }

        public static void LoadUrl(
            ChromiumWebBrowser browser,
            string? url,
            string requestMethod = "GET",
            string? referer = null,
            byte[]? postDataBytes = null)
        {
            if (browser == null || browser.IsDisposed || string.IsNullOrWhiteSpace(url))
                return;
            using var frame = browser.GetMainFrame();
            var initializePostData = string.Equals(requestMethod, "POST", StringComparison.OrdinalIgnoreCase);
            var request = frame.CreateRequest(initializePostData: initializePostData);
            if (initializePostData && postDataBytes is { Length: > 0 })
            {
                request.InitializePostData();
                request.PostData.AddData(postDataBytes);
            }
            request.Url = url;
            request.Method = string.IsNullOrWhiteSpace(requestMethod) ? "GET" : requestMethod;
            if (string.IsNullOrWhiteSpace(referer))
            {
                request.SetReferrer(referer, ReferrerPolicy.NeverClearReferrer);
            }
            frame.LoadRequest(request);
        }

        private Task PublishLogAsync(string message)
        {
            return PublishStatusAsync("log", true, message, CancellationToken.None);
        }

        private async Task PublishStatusAsync(
            string stage,
            bool success,
            string message,
            CancellationToken cancellationToken,
            JsonNode? data = null)
        {
            if (_statusChanged == null)
                return;

            var statusData = data?.DeepClone() as JsonObject ?? new JsonObject();
            statusData["stage"] = stage;
            statusData["browserId"] = BrowserId;

            try
            {
                await _statusChanged(new BrowserRunStatus
                {
                    BrowserId = BrowserId,
                    Stage = stage,
                    Success = success,
                    Message = message,
                    Data = statusData
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"{BrowserId}: publish status failed. stage={stage}, msg={ex.Message}");
            }
        }

        private async Task ConfigureProxyAsync(IRequestContext requestContext, JsonNode? payload, CancellationToken cancellationToken)
        {
            var proxyServer = payload?["proxy_server"]?.ToString();
            if (string.IsNullOrWhiteSpace(proxyServer))
                proxyServer = payload?["proxyServer"]?.ToString();

            var isProxyMode = GetBool(payload, "isProxyMode", false) || !string.IsNullOrWhiteSpace(proxyServer);
            if (!isProxyMode || string.IsNullOrWhiteSpace(proxyServer))
                return;

            await Cef.UIThreadTaskFactory.StartNew(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var preferences = new Dictionary<string, object>
                {
                    ["mode"] = "fixed_servers",
                    ["server"] = proxyServer
                };
                var success = requestContext.SetPreference("proxy", preferences, out var error);
                _log($"{BrowserId}: Set proxy server={proxyServer}, success={success}, error={error}");
            }, cancellationToken);
        }

        private static void TryStopBrowser(ChromiumWebBrowser browser)
        {
            try
            {
                if (!browser.IsDisposed)
                    browser.Stop();
            }
            catch
            {
            }
        }

        private async Task<bool> TryCaptureAndShowScreenshotAsync(ChromiumWebBrowser browser, int timeoutMs, CancellationToken cancellationToken)
        {
            if (browser.IsDisposed)
                return false;

            try
            {
                var host = browser.GetBrowserHost();
                host.Invalidate(PaintElementType.View);
                var screenshotBytes = await browser.CaptureScreenshotAsync()
                    .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (screenshotBytes == null || screenshotBytes.Length == 0)
                    return false;

                _screenshotReady(BrowserId, screenshotBytes);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"{BrowserId}: screenshot capture failed: {ex.Message}");
                return false;
            }
        }
        private static int GetSleepDelayMilliseconds(JsonNode? task)
        {
            var sleepText = GetNodeText(task?["sleep"]);
            if (string.IsNullOrWhiteSpace(sleepText))
                return 0;

            int seconds;
            var rangeParts = sleepText.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (rangeParts.Length == 2 &&
                int.TryParse(rangeParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minSeconds) &&
                int.TryParse(rangeParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxSeconds))
            {
                if (maxSeconds < minSeconds)
                {
                    (minSeconds, maxSeconds) = (maxSeconds, minSeconds);
                }

                if (maxSeconds <= 0)
                    return 0;

                minSeconds = Math.Max(0, minSeconds);
                var exclusiveMaxSeconds = maxSeconds == int.MaxValue ? int.MaxValue : maxSeconds + 1;
                seconds = Random.Shared.Next(minSeconds, exclusiveMaxSeconds);
            }
            else if (!int.TryParse(sleepText, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
            {
                return 0;
            }

            if (seconds <= 0)
                return 0;

            return seconds > int.MaxValue / 1000 ? int.MaxValue : seconds * 1000;
        }
        private static string GetNodeText(JsonNode? node)
        {
            if (node == null)
                return string.Empty;

            try
            {
                return node.GetValue<string>() ?? string.Empty;
            }
            catch
            {
                return node.ToString();
            }
        }
        private static string GetString(JsonNode? payload, string name, string defaultValue = "")
        {
            var node = payload?[name];
            if (node == null)
                return defaultValue;

            try
            {
                if (node is JsonArray array)
                    return array.FirstOrDefault()?.GetValue<string>() ?? defaultValue;

                return node.GetValue<string>() ?? defaultValue;
            }
            catch
            {
                return node.ToString();
            }
        }
        private static bool GetBool(JsonNode? payload, string name, bool defaultValue)
        {
            try
            {
                return payload?[name]?.GetValue<bool>() ?? defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }
        private static int GetPositiveInt(JsonNode? payload, string name, int defaultValue)
        {
            var value = GetNullableInt(payload, name);
            return value.HasValue && value.Value > 0 ? value.Value : defaultValue;
        }
        private static int? GetNullableInt(JsonNode? payload, string name)
        {
            try
            {
                return payload?[name]?.GetValue<int>();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// OSR 一次性浏览器不挂 UI，这里保留原调用点以兼容外部流程。
        /// </summary>
        public Task DetachFromUiAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// OSR 浏览器实例已在 RunAsync 内通过 using 释放，这里只做幂等标记。
        /// </summary>
        public Task DisposeHeavyAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeHeavyAsync();
        }
    }

    public sealed class BrowserRunStatus
    {
        public string BrowserId { get; set; } = "";
        public string Stage { get; set; } = "";
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public JsonNode? Data { get; set; }
    }

    public sealed class BrowserRunResult
    {
        public string BrowserId { get; set; } = "";
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public JsonNode? Data { get; set; }
    }
}
