

namespace CefClient
{
    using CefClient.Common;
    using CefSharp;
    using CefSharp.WinForms;
    using Microsoft.VisualBasic;
    using System.Collections.Specialized;
    using System.Diagnostics;
    using System.Globalization;
    using System.Net;
    using System.Text.Json.Nodes;

    public sealed class BrowserSlot : IAsyncDisposable
    {
        private const int DefaultInitialLoadTimeoutMs = 5000;
        public string BrowserId { get; }
        public Panel HostPanel { get; }
        public ChromiumWebBrowser Browser { get; }
        public IRequestContext RequestContext { get; }
        public string CachePath { get; }
        private readonly Control _parent;
        private readonly object _navigationStateLock = new();
        private int _disposed;
        private string _currentAddress = string.Empty;
        private string _lastMainFrameUrl = string.Empty;
        private string _lastMainFrameLoadErrorCode = string.Empty;
        private int _lastMainFrameLoadErrorCodeValue;
        private string _lastMainFrameLoadErrorText = string.Empty;
        private string _lastMainFrameFailedUrl = string.Empty;

        public BrowserSlot(
            string browserId,
            Panel hostPanel,
            ChromiumWebBrowser browser,
            IRequestContext requestContext,
            string cachePath,
            Control parent)
        {
            BrowserId = browserId;
            HostPanel = hostPanel;
            Browser = browser;
            RequestContext = requestContext;
            CachePath = cachePath;
            _parent = parent;
            Browser.AddressChanged += Browser_AddressChanged;
            Browser.FrameLoadStart += Browser_FrameLoadStart;
            Browser.LoadError += Browser_LoadError;
        }
        public async Task<bool> WaitForInitialLoadAsync(
            int timeoutMs = DefaultInitialLoadTimeoutMs,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Browser.WaitForInitialLoadAsync()
                    .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        private async Task<string> GetPageTitleAsync(ChromiumWebBrowser browser, int timeoutMs = 3000)
        {
            if (browser == null)
                return "";

            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var canExecute = await UiInvokeAsync(
                    () => !browser.IsDisposed && browser.CanExecuteJavascriptInMainFrame,
                    CancellationToken.None);

                if (!canExecute)
                {
                    await Task.Delay(100);
                    continue;
                }

                try
                {
                    var evaluateTask = await UiInvokeAsync(
                        () => browser.EvaluateScriptAsync("document.title"),
                        CancellationToken.None);
                    var result = await evaluateTask;
                    return result.Success ? result.Result?.ToString() ?? "" : "";
                }
                catch
                {
                    // 继续等一下再试
                }

                await Task.Delay(100);
            }

            return "";
        }


        public async Task<BrowserRunResult> RunAsync(
            JsonNode? payload,
            CancellationToken cancellationToken = default,
            Func<BrowserRunStatus, CancellationToken, Task>? statusChanged = null)
        {

            var task = payload?["task"];
            var sleepDelayMs = GetSleepDelayMilliseconds(task);
            var url = GetString(payload, "url");
            var referer = GetString(payload, "referer");
            if (string.IsNullOrWhiteSpace(referer))
                referer = GetString(task, "referer");

            var taskId = payload?["taskId"]?.ToString() ?? string.Empty;
            var consumerId = payload?["consumerId"]?.ToString() ?? "unknown";
            var uvIndex = payload?["uvIndex"]?.ToString() ?? BrowserId;
            var loadTimeoutMs = GetPositiveInt(payload, "loadTimeoutMs", 30000);
            var pvTotal = GetPositiveInt(task, "pv", 1);
            var pvIntervalMs = GetNonNegativeInt(payload, "pvIntervalMs", 1000);
            BrowserRunResult? result = null;
            var completedPv = 0;

            Task PublishLogAsync(string message)
            {
                return PublishStatusAsync(statusChanged, "log", true, message, CancellationToken.None);
            }

            try
            {

                await PublishStatusAsync(statusChanged, "start", true, "browser started", cancellationToken, BuildRunData(
                    url,
                    referer,
                    sleepDelayMs,
                    taskId: taskId,
                    consumerId: consumerId,
                    uvIndex: uvIndex,
                    loadTimeoutMs: loadTimeoutMs,
                    pvTotal: pvTotal,
                    completedPv: completedPv,
                    pvIntervalMs: pvIntervalMs));

                if (string.IsNullOrWhiteSpace(url))
                {
                    result = new BrowserRunResult
                    {
                        BrowserId = BrowserId,
                        Success = false,
                        Message = "url 不能为空",
                        Data = BuildRunData(url, referer, sleepDelayMs, taskId: taskId, consumerId: consumerId, uvIndex: uvIndex, loadTimeoutMs: loadTimeoutMs, pvTotal: pvTotal, completedPv: completedPv, pvIntervalMs: pvIntervalMs)
                    };

                    await PublishStatusAsync(statusChanged, "error", false, result.Message, cancellationToken, result.Data);
                    return result;
                }

                try
                {
                    await Browser.WaitForInitialLoadAsync()
                        .WaitAsync(TimeSpan.FromMilliseconds(DefaultInitialLoadTimeoutMs), cancellationToken);
                }
                catch (TimeoutException)
                {
                    result = new BrowserRunResult
                    {
                        BrowserId = BrowserId,
                        Success = false,
                        Message = "浏览器初始加载超时",
                        Data = BuildRunData(url, referer, sleepDelayMs, taskId: taskId, consumerId: consumerId, uvIndex: uvIndex, loadTimeoutMs: loadTimeoutMs, pvTotal: pvTotal, completedPv: completedPv, pvIntervalMs: pvIntervalMs)
                    };

                    await PublishStatusAsync(statusChanged, "error", false, result.Message, cancellationToken, result.Data);
                    return result;
                }

                var proxyInfo = await ConfigureProxyAsync(payload, PublishLogAsync, cancellationToken);
                var deviceInfo = await ConfigureMobileEmulationAsync(payload, PublishLogAsync, cancellationToken);

                WaitForNavigationAsyncResponse? lastLoadResponse = null;
                var lastLoadTimedOut = false;
                var finalLoadCompleted = false;


                for (var pvIndex = 1; pvIndex <= pvTotal; pvIndex++)
                {
                    ResetNavigationDiagnostics(url);

                    var navigationTask = await UiInvokeAsync(
                        () => Browser.WaitForNavigationAsync(
                            TimeSpan.FromMilliseconds(loadTimeoutMs),
                            cancellationToken),
                        cancellationToken);



                    if (!string.IsNullOrWhiteSpace(referer))
                    {
                        LoadUrl(Browser, url, "GET", referrer: referer);
                    }
                    else
                    {
                        Browser.Load(url);
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
                        await PublishLogAsync($"PV {pvIndex}/{pvTotal} navigation timeout after {loadTimeoutMs}ms. {GetNavigationDiagnosticsSummary(loadResponse)}");
                        await UiInvokeAsync(() => TryStopBrowser(Browser), CancellationToken.None);
                    }

                    var loadFailed = loadResponse != null && loadResponse.ErrorCode != CefErrorCode.None;
                    var loadCompleted = loadResponse != null && !loadTimedOut && loadResponse.ErrorCode == CefErrorCode.None;
                    lastLoadResponse = loadResponse;
                    lastLoadTimedOut = loadTimedOut;
                    finalLoadCompleted = loadCompleted;
                    completedPv = pvIndex;

                    var pvData = BuildRunData(
                        url,
                        referer,
                        sleepDelayMs,
                        taskId: taskId,
                        consumerId: consumerId,
                        uvIndex: uvIndex,
                        loadCompleted: loadCompleted,
                        loadTimedOut: loadTimedOut,
                        loadErrorCode: loadResponse?.ErrorCode.ToString() ?? string.Empty,
                        httpStatusCode: loadResponse?.HttpStatusCode ?? -1,
                        loadTimeoutMs: loadTimeoutMs,
                        pvIndex: pvIndex,
                        pvTotal: pvTotal,
                        completedPv: completedPv,
                        pvIntervalMs: pvIntervalMs,
                        failedUrl: GetLastMainFrameFailedUrl(),
                        loadErrorText: GetLastMainFrameLoadErrorText(),
                        currentAddress: GetCurrentAddress(),
                        proxyServer: proxyInfo.ProxyServer,
                        proxyApplied: proxyInfo.Applied,
                        proxyError: proxyInfo.Error,
                        deviceWidth: deviceInfo.Width,
                        deviceHeight: deviceInfo.Height,
                        cssWidth: deviceInfo.CssWidth,
                        cssHeight: deviceInfo.CssHeight,
                        deviceScaleFactor: deviceInfo.DeviceScaleFactor,
                        platform: deviceInfo.Platform,
                        userAgent: deviceInfo.UserAgent);




                    if (loadFailed)
                    {
                        var failureMessage = GetNavigationFailureMessage(loadResponse);
                        await PublishLogAsync($"PV {pvIndex}/{pvTotal} load failed. {GetNavigationDiagnosticsSummary(loadResponse)}");

                        result = new BrowserRunResult
                        {
                            BrowserId = BrowserId,
                            Success = false,
                            Message = $"页面加载失败,{failureMessage}",
                            Data = pvData
                        };

                        await PublishStatusAsync(statusChanged, "error", false, $"第 {pvIndex}/{pvTotal} 次 PV 页面加载失败,{failureMessage}", cancellationToken, pvData);



                        return result;
                    }

                    await PublishStatusAsync(statusChanged, "pv", true, loadCompleted ? $"pv {pvIndex}/{pvTotal} opened" : $"pv {pvIndex}/{pvTotal} 页面加载较慢，已按超时继续", cancellationToken, pvData);
                    if (pvIndex < pvTotal && pvIntervalMs > 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(pvIntervalMs), cancellationToken);
                    }
                }

                var dspData = BuildRunData(
                    url,
                    referer,
                    sleepDelayMs,
                    taskId: taskId,
                    consumerId: consumerId,
                    uvIndex: uvIndex,
                    loadCompleted: finalLoadCompleted,
                    loadTimedOut: lastLoadTimedOut,
                    loadErrorCode: lastLoadResponse?.ErrorCode.ToString() ?? string.Empty,
                    httpStatusCode: lastLoadResponse?.HttpStatusCode ?? -1,
                    loadTimeoutMs: loadTimeoutMs,
                    pvTotal: pvTotal,
                    completedPv: completedPv,
                    pvIntervalMs: pvIntervalMs,
                    proxyServer: proxyInfo.ProxyServer,
                    proxyApplied: proxyInfo.Applied,
                    proxyError: proxyInfo.Error,
                    deviceWidth: deviceInfo.Width,
                    deviceHeight: deviceInfo.Height,
                    cssWidth: deviceInfo.CssWidth,
                    cssHeight: deviceInfo.CssHeight,
                    deviceScaleFactor: deviceInfo.DeviceScaleFactor,
                    platform: deviceInfo.Platform,
                    userAgent: deviceInfo.UserAgent);

                await PublishStatusAsync(statusChanged, "dsp", true, finalLoadCompleted ? "page opened" : "页面加载较慢，已按超时继续", cancellationToken, dspData);

                var title = await GetPageTitleAsync(Browser);
                var successData = BuildRunData(
                    url,
                    referer,
                    sleepDelayMs,
                    title,
                    taskId,
                    consumerId,
                    uvIndex,
                    finalLoadCompleted,
                    lastLoadTimedOut,
                    lastLoadResponse?.ErrorCode.ToString() ?? string.Empty,
                    lastLoadResponse?.HttpStatusCode ?? -1,
                    loadTimeoutMs,
                    pvTotal: pvTotal,
                    completedPv: completedPv,
                    pvIntervalMs: pvIntervalMs,
                    proxyServer: proxyInfo.ProxyServer,
                    proxyApplied: proxyInfo.Applied,
                    proxyError: proxyInfo.Error,
                    deviceWidth: deviceInfo.Width,
                    deviceHeight: deviceInfo.Height,
                    cssWidth: deviceInfo.CssWidth,
                    cssHeight: deviceInfo.CssHeight,
                    deviceScaleFactor: deviceInfo.DeviceScaleFactor,
                    platform: deviceInfo.Platform,
                    userAgent: deviceInfo.UserAgent);

                await PublishStatusAsync(statusChanged, "success", true, "执行成功", cancellationToken, successData);

                result = new BrowserRunResult
                {
                    BrowserId = BrowserId,
                    Success = true,
                    Message = finalLoadCompleted ? "执行成功" : "页面加载较慢，已按超时继续",
                    Data = successData
                };

                return result;
            }
            catch (OperationCanceledException)
            {
                result = new BrowserRunResult
                {
                    BrowserId = BrowserId,
                    Success = false,
                    Message = "取消",
                    Data = BuildRunData(url, referer, sleepDelayMs, taskId: taskId, consumerId: consumerId, uvIndex: uvIndex, loadTimeoutMs: loadTimeoutMs, pvTotal: pvTotal, completedPv: completedPv, pvIntervalMs: pvIntervalMs)
                };
                await PublishStatusAsync(statusChanged, "error", false, "取消", CancellationToken.None, result.Data);
                return result;
            }
            catch (Exception ex)
            {
                result = new BrowserRunResult
                {
                    BrowserId = BrowserId,
                    Success = false,
                    Message = ex.Message,
                    Data = BuildRunData(url, referer, sleepDelayMs, taskId: taskId, consumerId: consumerId, uvIndex: uvIndex, loadTimeoutMs: loadTimeoutMs, pvTotal: pvTotal, completedPv: completedPv, pvIntervalMs: pvIntervalMs)
                };

                await PublishStatusAsync(statusChanged, "error", false, ex.Message, CancellationToken.None, result.Data);
                return result;
            }
            finally
            {
                if (sleepDelayMs > 0)
                {
                    await Task.Delay(sleepDelayMs, CancellationToken.None);
                }

                await PublishStatusAsync(statusChanged, "complete", true, "RunAsync complete", CancellationToken.None, BuildRunData(
                    url,
                    referer,
                    sleepDelayMs,
                    taskId: taskId,
                    consumerId: consumerId,
                    uvIndex: uvIndex,
                    loadTimeoutMs: loadTimeoutMs,
                    pvTotal: pvTotal,
                    completedPv: completedPv,
                    pvIntervalMs: pvIntervalMs));
            }
        }


        private JsonObject BuildRunData(
            string? url,
            string? referer,
            int sleepDelayMs,
            string? title = null,
            string? taskId = null,
            string? consumerId = null,
            string? uvIndex = null,
            bool? loadCompleted = null,
            bool? loadTimedOut = null,
            string? loadErrorCode = null,
            int? httpStatusCode = null,
            int? loadTimeoutMs = null,
            int? pvIndex = null,
            int? pvTotal = null,
            int? completedPv = null,
            int? pvIntervalMs = null,
            string? failedUrl = null,
            string? loadErrorText = null,
            string? currentAddress = null,
            string? ignoredRefererReason = null,
            string? proxyServer = null,
            bool? proxyApplied = null,
            string? proxyError = null,
            int? deviceWidth = null,
            int? deviceHeight = null,
            int? cssWidth = null,
            int? cssHeight = null,
            float? deviceScaleFactor = null,
            string? platform = null,
            string? userAgent = null)
        {
            var data = new JsonObject
            {
                ["url"] = url ?? string.Empty,
                ["referer"] = referer ?? string.Empty,
                ["cachePath"] = CachePath,
                ["sleepDelayMs"] = sleepDelayMs
            };

            if (!string.IsNullOrWhiteSpace(taskId))
                data["taskId"] = taskId;
            if (!string.IsNullOrWhiteSpace(consumerId))
                data["consumerId"] = consumerId;
            if (!string.IsNullOrWhiteSpace(uvIndex))
                data["uvIndex"] = uvIndex;
            if (title != null)
                data["title"] = title;
            if (loadCompleted.HasValue)
                data["loadCompleted"] = loadCompleted.Value;
            if (loadTimedOut.HasValue)
                data["loadTimedOut"] = loadTimedOut.Value;
            if (loadErrorCode != null)
                data["loadErrorCode"] = loadErrorCode;
            if (httpStatusCode.HasValue)
                data["httpStatusCode"] = httpStatusCode.Value;
            if (loadTimeoutMs.HasValue)
                data["loadTimeoutMs"] = loadTimeoutMs.Value;
            if (pvIndex.HasValue)
                data["pvIndex"] = pvIndex.Value;
            if (pvTotal.HasValue)
                data["pvTotal"] = pvTotal.Value;
            if (completedPv.HasValue)
                data["completedPv"] = completedPv.Value;
            if (pvIntervalMs.HasValue)
                data["pvIntervalMs"] = pvIntervalMs.Value;
            if (!string.IsNullOrWhiteSpace(failedUrl))
                data["failedUrl"] = failedUrl;
            if (!string.IsNullOrWhiteSpace(loadErrorText))
                data["loadErrorText"] = loadErrorText;
            if (!string.IsNullOrWhiteSpace(currentAddress))
                data["currentAddress"] = currentAddress;
            if (!string.IsNullOrWhiteSpace(ignoredRefererReason))
                data["ignoredRefererReason"] = ignoredRefererReason;
            if (!string.IsNullOrWhiteSpace(proxyServer))
                data["proxyServer"] = proxyServer;
            if (proxyApplied.HasValue)
                data["proxyApplied"] = proxyApplied.Value;
            if (!string.IsNullOrWhiteSpace(proxyError))
                data["proxyError"] = proxyError;
            if (deviceWidth.HasValue)
                data["deviceWidth"] = deviceWidth.Value;
            if (deviceHeight.HasValue)
                data["deviceHeight"] = deviceHeight.Value;
            if (cssWidth.HasValue)
                data["cssWidth"] = cssWidth.Value;
            if (cssHeight.HasValue)
                data["cssHeight"] = cssHeight.Value;
            if (deviceScaleFactor.HasValue)
                data["deviceScaleFactor"] = (double)deviceScaleFactor.Value;
            if (!string.IsNullOrWhiteSpace(platform))
                data["platform"] = platform;
            if (!string.IsNullOrWhiteSpace(userAgent))
                data["userAgent"] = userAgent;

            return data;
        }

        private async Task<ProxyConfigurationInfo> ConfigureProxyAsync(
            JsonNode? payload,
            Func<string, Task> publishLogAsync,
            CancellationToken cancellationToken)
        {
            var proxyServer = GetString(payload, "proxy_server");
            if (string.IsNullOrWhiteSpace(proxyServer))
                proxyServer = GetString(payload, "proxyServer");

            var isProxyMode = GetBool(payload, "isProxyMode", false) || !string.IsNullOrWhiteSpace(proxyServer);
            if (!isProxyMode || string.IsNullOrWhiteSpace(proxyServer))
                return new ProxyConfigurationInfo(proxyServer, false, string.Empty);

            bool success = false;
            string error = string.Empty;
            await Cef.UIThreadTaskFactory.StartNew(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var preferences = new Dictionary<string, object>
                {
                    ["mode"] = "fixed_servers",
                    ["server"] = proxyServer
                };

                success = RequestContext.SetPreference("proxy", preferences, out error);
            });

            await publishLogAsync($"Set proxy server={proxyServer}, success={success}, error={error}");
            return new ProxyConfigurationInfo(proxyServer, success, error);
        }

        private async Task<DeviceConfigurationInfo> ConfigureMobileEmulationAsync(
            JsonNode? payload,
            Func<string, Task> publishLogAsync,
            CancellationToken cancellationToken)
        {
            var os = GetNullableInt(payload, "os") ?? 0;
            var device = payload?["device"];
            var sw = GetNullableInt(device, "sw") ?? 1080;
            var sh = GetNullableInt(device, "sh") ?? 1920;
            var ua = GetString(device, "ua");
            if (string.IsNullOrWhiteSpace(ua))
                ua = GetString(payload, "userAgent");

            var platform = os == 1 ? "Android" : "iPhone";
            var devProfile = DeviceViewportMatcher.Match(sw, sh, os == 2 ? DeviceSystemType.IOS : DeviceSystemType.Android);

            float dpiScale;
            using (var g = Browser.CreateGraphics())
            {
                dpiScale = g.DpiX / 96f;
            }
            int controlWidth = (int)Math.Ceiling(devProfile.ViewportWidth * dpiScale);
            int controlHeight = (int)Math.Ceiling(devProfile.ViewportHeight * dpiScale);

            await UiInvokeAsync(() =>
            {
 
                Browser.Size = new Size(controlWidth,controlHeight);
                //HostPanel.Width = controlWidth + 8;
                //HostPanel.Height = controlHeight + 8;
            }, cancellationToken);

            using var devToolsClient = await UiInvokeAsync(() => Browser.GetDevToolsClient(), cancellationToken);

            if (GetBool(payload, "clearStorage", false))
            {
                await devToolsClient.Storage.ClearDataForOriginAsync("*", "cache_storage,cookies,local_storage");
            }

            if (!string.IsNullOrWhiteSpace(ua))
            {
                await devToolsClient.Emulation.SetUserAgentOverrideAsync(userAgent: ua, platform: platform);
            }

            await devToolsClient.Emulation.SetDeviceMetricsOverrideAsync(
                width: devProfile.ViewportWidth,
                height: devProfile.ViewportHeight,
                deviceScaleFactor: devProfile.DeviceScaleFactor,
                mobile: true,
                scale: 1.0,
                screenWidth: devProfile.ViewportWidth,
                screenHeight: devProfile.ViewportHeight,
                positionX: 0,
                positionY: 0,
                screenOrientation: new CefSharp.DevTools.Emulation.ScreenOrientation() { Type = CefSharp.DevTools.Emulation.ScreenOrientationType.PortraitPrimary, Angle = 0 });




            //await devToolsClient.Emulation.SetDeviceMetricsOverrideAsync(
            //    width: devProfile.ViewportWidth,
            //    height: devProfile.ViewportHeight,
            //    deviceScaleFactor: devProfile.DeviceScaleFactor,
            //    mobile: true,
            //    scale: 1.0,
            //    //screenWidth: devProfile.ViewportWidth,
            //    //screenHeight: devProfile.ViewportHeight,
            //    positionX: 0,
            //    positionY: 0,
            //    screenOrientation: new CefSharp.DevTools.Emulation.ScreenOrientation() { Type = CefSharp.DevTools.Emulation.ScreenOrientationType.PortraitPrimary, Angle = 0 });


            await devToolsClient.Emulation.SetTouchEmulationEnabledAsync(true, Random.Shared.Next(4, 6));
            await devToolsClient.Emulation.SetScrollbarsHiddenAsync(true);

            await publishLogAsync($"Mobile emulation configured. device={sw}x{sh}, css={devProfile.ViewportWidth}x{devProfile.ViewportHeight}, dpr={devProfile.DeviceScaleFactor}, platform={platform}, ua={ua}");
            return new DeviceConfigurationInfo(sw, sh, devProfile.ViewportWidth, devProfile.ViewportHeight, devProfile.DeviceScaleFactor, platform, ua);
        }

        private void Browser_AddressChanged(object? sender, AddressChangedEventArgs e)
        {
            lock (_navigationStateLock)
            {
                _currentAddress = e.Address ?? string.Empty;
            }
        }

        private void Browser_FrameLoadStart(object? sender, FrameLoadStartEventArgs e)
        {
            if (!e.Frame.IsMain)
                return;

            lock (_navigationStateLock)
            {
                _lastMainFrameUrl = e.Url ?? string.Empty;
            }
        }

        private void Browser_LoadError(object? sender, LoadErrorEventArgs e)
        {
            if (!e.Frame.IsMain)
                return;

            lock (_navigationStateLock)
            {
                _lastMainFrameLoadErrorCode = e.ErrorCode.ToString();
                _lastMainFrameLoadErrorCodeValue = (int)e.ErrorCode;
                _lastMainFrameLoadErrorText = e.ErrorText ?? string.Empty;
                _lastMainFrameFailedUrl = e.FailedUrl ?? string.Empty;
            }
        }

        private void ResetNavigationDiagnostics(string? requestedUrl)
        {
            lock (_navigationStateLock)
            {
                _lastMainFrameUrl = requestedUrl ?? string.Empty;
                _lastMainFrameLoadErrorCode = string.Empty;
                _lastMainFrameLoadErrorCodeValue = 0;
                _lastMainFrameLoadErrorText = string.Empty;
                _lastMainFrameFailedUrl = string.Empty;
            }
        }

        private string GetCurrentAddress()
        {
            lock (_navigationStateLock)
            {
                return _currentAddress;
            }
        }

        private string GetLastMainFrameFailedUrl()
        {
            lock (_navigationStateLock)
            {
                return _lastMainFrameFailedUrl;
            }
        }

        private string GetLastMainFrameLoadErrorText()
        {
            lock (_navigationStateLock)
            {
                return _lastMainFrameLoadErrorText;
            }
        }

        private string GetNavigationDiagnosticsSummary(WaitForNavigationAsyncResponse? loadResponse)
        {
            lock (_navigationStateLock)
            {
                var errorCode = loadResponse?.ErrorCode.ToString() ?? _lastMainFrameLoadErrorCode;
                var errorCodeValue = loadResponse != null ? (int)loadResponse.ErrorCode : _lastMainFrameLoadErrorCodeValue;
                var httpStatus = loadResponse?.HttpStatusCode ?? -1;
                return $"error={errorCode}({errorCodeValue}), httpStatus={httpStatus}, errorText={_lastMainFrameLoadErrorText}, failedUrl={_lastMainFrameFailedUrl}, currentAddress={_currentAddress}, frameUrl={_lastMainFrameUrl}";
            }
        }

        private string GetNavigationFailureMessage(WaitForNavigationAsyncResponse? loadResponse)
        {
            lock (_navigationStateLock)
            {
                var errorCode = loadResponse?.ErrorCode.ToString() ?? _lastMainFrameLoadErrorCode;
                var errorText = string.IsNullOrWhiteSpace(_lastMainFrameLoadErrorText) ? "无 HTTP 响应" : _lastMainFrameLoadErrorText;
                var failedUrl = string.IsNullOrWhiteSpace(_lastMainFrameFailedUrl) ? _lastMainFrameUrl : _lastMainFrameFailedUrl;
                return $"{errorCode}: {errorText}, failedUrl={failedUrl}";
            }
        }

        private static string? ValidateHttpNavigationUrl(string? url, string name)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return $"{name} 格式不正确，必须是完整的 http/https 地址: {url}";

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return $"{name} 协议不支持，仅支持 http/https: {url}";
            }

            return null;
        }

        private static string? GetUsableReferer(string? referer, out string? ignoredReason)
        {
            ignoredReason = null;
            if (string.IsNullOrWhiteSpace(referer))
                return null;

            var validationError = ValidateHttpNavigationUrl(referer, "referer");
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                ignoredReason = validationError;
                return null;
            }

            return referer;
        }

        private WebHeaderCollection? BuildRefererHeaders(string? referer)
        {
            if (string.IsNullOrWhiteSpace(referer))
                return null;

            return new WebHeaderCollection
            {

            };
        }

        public void LoadUrl(
            ChromiumWebBrowser browser,
            string? url,
            string requestMethod = "GET",
            string? referrer = null,
            WebHeaderCollection? headers = null,
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

            if (!string.IsNullOrWhiteSpace(referrer))
            {
                request.SetReferrer(
                    referrer,
                    ReferrerPolicy.NeverClearReferrer
                );
            }

            if (headers != null && headers.HasKeys())
            {
                var originHeaders = request.Headers ?? new NameValueCollection();
                foreach (string keyName in headers.AllKeys)
                {
                    originHeaders.Set(keyName, headers[keyName]);
                }
                request.Headers = originHeaders;
            }

            frame.LoadRequest(request);
        }



        private async Task PublishStatusAsync(
            Func<BrowserRunStatus, CancellationToken, Task>? statusChanged,
            string stage,
            bool success,
            string message,
            CancellationToken cancellationToken,
            JsonNode? data = null)
        {
            if (statusChanged == null)
                return;

            var statusData = data?.DeepClone() as JsonObject ?? new JsonObject();
            statusData["stage"] = stage;
            statusData["browserId"] = BrowserId;

            try
            {
                await statusChanged(new BrowserRunStatus
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
                Debug.WriteLine($"{BrowserId}: publish status failed. stage={stage}, msg={ex.Message}");
            }
        }

        private static void TryStopBrowser(ChromiumWebBrowser browser)
        {
            try
            {
                if (!browser.IsDisposed && browser.IsBrowserInitialized)
                    browser.Stop();
            }
            catch
            {
            }
        }

        private Task UiInvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            return UiInvokeAsync(() =>
            {
                action();
                return true;
            }, cancellationToken);
        }

        private Task<T> UiInvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (_parent.IsDisposed || _parent.Disposing)
            {
                tcs.TrySetException(new ObjectDisposedException(nameof(_parent)));
                return tcs.Task;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return tcs.Task;
            }

            void Execute()
            {
                try
                {
                    if (_parent.IsDisposed || _parent.Disposing)
                    {
                        tcs.TrySetException(new ObjectDisposedException(nameof(_parent)));
                        return;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                        return;
                    }

                    tcs.TrySetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }

            try
            {
                if (_parent.InvokeRequired)
                {
                    _parent.BeginInvoke((Action)Execute);
                }
                else
                {
                    Execute();
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }

            return tcs.Task;
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

        private static int GetNonNegativeInt(JsonNode? payload, string name, int defaultValue)
        {
            var value = GetNullableInt(payload, name);
            return value.HasValue && value.Value >= 0 ? value.Value : defaultValue;
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
        /// 只从界面移除，不做重释放
        /// </summary>
        public Task DetachFromUiAsync()
        {
            try
            {
                if (_parent.Controls.Contains(HostPanel))
                    _parent.Controls.Remove(HostPanel);
            }
            catch
            {
            }

            return Task.CompletedTask;
        }

        private void DetachBrowserEvents()
        {
            Browser.AddressChanged -= Browser_AddressChanged;
            Browser.FrameLoadStart -= Browser_FrameLoadStart;
            Browser.LoadError -= Browser_LoadError;
        }

        /// <summary>
        /// 真正做重释放，建议后台调用
        /// </summary>
        public async Task DisposeHeavyAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                await UiInvokeAsync(() =>
                {
                    if (HostPanel.Controls.Contains(Browser))
                        HostPanel.Controls.Remove(Browser);

                    DetachBrowserEvents();
                    Browser.Dispose();
                    HostPanel.Dispose();
                });
            }
            catch
            {
            }

            // IRequestContext 一般不用你手动 Dispose
            // 如果你当前版本支持并且你确认需要，也可以自己补

            await Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await UiInvokeAsync(() =>
                {
                    if (_parent.Controls.Contains(HostPanel))
                        _parent.Controls.Remove(HostPanel);

                    HostPanel.Controls.Remove(Browser);
                    DetachBrowserEvents();
                    Browser.Dispose();
                    HostPanel.Dispose();
                });
            }
            catch { }

            await Task.CompletedTask;
        }
    }

    public sealed record ProxyConfigurationInfo(string ProxyServer, bool Applied, string Error);

    public sealed record DeviceConfigurationInfo(
        int Width,
        int Height,
        int CssWidth,
        int CssHeight,
        float DeviceScaleFactor,
        string Platform,
        string UserAgent);

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
