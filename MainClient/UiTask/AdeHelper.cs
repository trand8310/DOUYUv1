

using MainClient.Common;
using MainClient.Infrastructure;
using MainClient.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;


namespace MainClient.UiTask;




public class AdeHelper
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppSettings _appSettings;
    private readonly ILogger _logger;
    private readonly AdeOptions _options;

    public static HttpClient client = new HttpClient();
    public const string _apiVersion = "_v2";
    public AdeHelper(IHttpClientFactory httpClientFactory, AppSettings appSettings, ILogger<AdeHelper> logger, IOptions<AdeOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _appSettings = appSettings;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// 获取任务
    /// </summary>
    /// <param name="address"></param>
    /// <returns></returns>
    public async Task<string?> GetTaskAsync(string address, CancellationToken token = default)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await client.GetAsync(address, token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(token);
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, $"GetTaskAsync request failed : {httpEx.Message}");
        }
        catch (TaskCanceledException cancelEx) when (!token.IsCancellationRequested)
        {
            _logger.LogError(cancelEx, $"GetTaskAsync request timeout : {cancelEx.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"GetTaskAsync Unexpected : {ex.Message}");
        }

        return null;
    }

    #region 系统设备
    private static ConcurrentQueue<JsonNode> ANDROID_QUEUE = new();
    private static ConcurrentQueue<JsonNode> iOS_QUEUE = new();
    private readonly SemaphoreSlim iOS_SIGNAL = new(1, 1);
    private readonly SemaphoreSlim ANDROID_SIGNAL = new(1, 1);
    private async Task<string?> GetDevByOSInternal(OSType os, int count)
    {
        try
        {
            var devApiUrl = _appSettings.DevApiUrl;
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
            var type = os == OSType.IOS ? "ios" : os == OSType.PC ? "win" : "android";

            var url = $"{devApiUrl}?type={type}&count={count}&t={System.DateTime.Now.Ticks}";
            HttpResponseMessage response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ex.Message);
        }
        return null;
    }
    public async Task<JsonNode?> GetDevByOS(OSType os, int count = 5)
    {
        (ConcurrentQueue<JsonNode> devs, SemaphoreSlim sem) =
            os == OSType.IOS ?
            (iOS_QUEUE, iOS_SIGNAL) :
            (ANDROID_QUEUE, ANDROID_SIGNAL);

        if (devs.TryDequeue(out var cached))
        {
            return cached;
        }
        await sem.WaitAsync();
        try
        {
            if (devs.TryDequeue(out cached))
            {
                return cached;
            }
            var text = await GetDevByOSInternal(os, count);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            var json = JsonNode.Parse(text);
            var data = json["data"] as JsonArray;
            if (data == null || data.Count == 0)
            {
                return null;
            }
            JsonNode first = data[0];
            for (int i = 1; i < data.Count; i++)
            {
                devs.Enqueue(data[i]);
            }

            return first;
        }
        catch (Exception ex)
        {
            _logger.LogError($"GetDevByOS error: {ex.Message}");
            return null;
        }
        finally
        {
            sem.Release();
        }
    }

    public OSType GetOS(string devClientId)
    {
        return devClientId switch
        {
            "7" => OSType.PC,
            "4" => OSType.IOS,
            _ => OSType.ANDROID
        };
    }
    public async Task<JsonNode?> GetDeviceAsync(OSType os, int count)
    {
        int retry = 0;
        JsonNode? dev = null;
        while (retry++ < 5)
        {
            dev = await GetDevByOS(os, count);
            if (dev != null) break;
        }
        return dev;
    }

    #endregion

    #region  任务状态统计&更新

    /// <summary>
    /// 更新任务状态
    /// </summary>
    /// <param name="taskId">任务 ID</param>
    /// <param name="metrics">指标字典，例如 start, dsp, click, success</param>
    /// <param name="token">取消令牌</param>
    /// <returns></returns>
    public async Task<JsonNode?> UpdateTaskStateAsync(int taskId, Dictionary<string, long> metrics, CancellationToken token = default)
    {
        return null;
        try
        {
            var host = await CommonHelper.GetLocalHostAsync();
            var baseUrl = new Uri(_appSettings.TaskApiUrl).GetLeftPart(UriPartial.Authority);
            StringBuilder builder = new StringBuilder(baseUrl);
            builder.Append($"/api{_apiVersion}/task-status.php?action=update_task&_t={System.DateTime.Now.Ticks}");
            var bidRequest = new
            {
                id = taskId,
                host = host,
                version = _options.AppVersion,
                metrics = metrics
            };
            var postData = JsonSerializer.Serialize(bidRequest);
            using var content = new StringContent(postData, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(builder.ToString(), content, token);
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(token))?.AsObject();
        }
        catch (OperationCanceledException)
        {
            // 请求被取消，安全退出
        }
        catch (Exception ex)
        {
            _logger.LogError($"UpdateTaskStateAsync TaskId={taskId} failed: {ex.Message}");
        }
        return null;
    }


    /// <summary>
    /// 获取当前任务的状态
    /// </summary>
    /// <param name="taskId"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    public async Task<JsonNode?> GetTaskStatusAsync(int taskId, CancellationToken token = default)
    {
        try
        {
            var host = await CommonHelper.GetLocalHostAsync();
            var baseUrl = new Uri(_appSettings.TaskApiUrl).GetLeftPart(UriPartial.Authority);
            using var response = await client.GetAsync($"{baseUrl}/api{_apiVersion}/task-status.php?action=task_status&id={taskId}&host={System.Web.HttpUtility.UrlEncode(host)}&_t={System.DateTime.Now.Ticks}", token);
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(token))?.AsObject();
        }
        catch (OperationCanceledException)
        {
            // 请求被取消，安全退出
        }
        catch (Exception ex)
        {
            _logger.LogError($"GetTaskStatusAsync TaskId={taskId} failed: {ex.Message}");
        }
        return null;
    }




    #endregion

    #region  主机状态统计&更新

    private static string GetProxyHostSafely(string? proxyIpUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyIpUrl))
            return string.Empty;

        return Uri.TryCreate(proxyIpUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : string.Empty;
    }


    /// <summary>
    /// 更新主机状态
    /// </summary>
    /// <param name="metrics"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    public async Task<JsonNode?> UpdateHostStateAsync(Dictionary<string, long> metrics, CancellationToken token = default)
    {
        metrics ??= new Dictionary<string, long>();
        string wordName = "default";
        var host = await CommonHelper.GetLocalHostAsync();
        return null;
        try
        {
            var baseUrl = new Uri(_appSettings.TaskApiUrl).GetLeftPart(UriPartial.Authority);
            StringBuilder builder = new StringBuilder(baseUrl);
            builder.Append($"/api{_apiVersion}/task-status.php?action=update_host&_t={System.DateTime.Now.Ticks}");
            var bidRequest = new
            {
                host = host,
                task = _appSettings.TaskName,
                version = _options.AppVersion,
                proxy = GetProxyHostSafely(_appSettings.ProxyIpUrl),
                fullproxy = _appSettings.ProxyIpUrl,
                wordname = wordName,
                metrics = metrics,
            };
            var postData = JsonSerializer.Serialize(bidRequest);
            using var content = new StringContent(postData, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(builder.ToString(), content, token);
            response.EnsureSuccessStatusCode();
            var resp = await response.Content.ReadAsStringAsync(token);
            return JsonNode.Parse(resp)?.AsObject();
        }
        catch (OperationCanceledException)
        {
            // 请求被取消，安全退出
        }
        catch (Exception ex)
        {
            _logger.LogError($"UpdateHostStateAsync Host={host} failed: {ex.Message}");
        }
        return null;
    }


    /// <summary>
    /// 获取今日主机状态
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    public async Task<JsonNode?> GetHostTodayStatusAsync(CancellationToken token = default)
    {
        var host = await CommonHelper.GetLocalHostAsync();
        try
        {
            var baseUrl = new Uri(_appSettings.TaskApiUrl).GetLeftPart(UriPartial.Authority);
            using var response = await client.GetAsync($"{baseUrl}/api{_apiVersion}/task-status.php?action=host_today_status&host={System.Web.HttpUtility.UrlEncode(host)}&_t={System.DateTime.Now.Ticks}", token);
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(token))?.AsObject();
        }
        catch (OperationCanceledException)
        {
            // 请求被取消，安全退出
        }
        catch (Exception ex)
        {
            _logger.LogError($"GetHostTodayStatusAsync Host={host} failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 获取当前时段主机状态
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    public async Task<JsonNode?> GetHostHourStatusAsync(CancellationToken token = default)
    {
        var host = await CommonHelper.GetLocalHostAsync();
        try
        {
            var baseUrl = new Uri(_appSettings.TaskApiUrl).GetLeftPart(UriPartial.Authority);
            using var response = await client.GetAsync($"{baseUrl}/api{_apiVersion}/task-status.php?action=host_hour_status&host={System.Web.HttpUtility.UrlEncode(host)}&_t={System.DateTime.Now.Ticks}", token);
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(token))?.AsObject();
        }
        catch (OperationCanceledException)
        {
            // 请求被取消，安全退出
        }
        catch (Exception ex)
        {
            _logger.LogError($"GetHostHourStatusAsync Host={host} failed: {ex.Message}");
        }
        return null;
    }

    #endregion

    #region 代理状态统计&更新
    public async Task<JsonNode?> UpdateProxyIpStateAsync(int taskId, Dictionary<string, long> metrics, IEnumerable<string> ips, CancellationToken token = default)
    {
        try
        {
            var host = await CommonHelper.GetLocalHostAsync();
            var baseUrl = new Uri(_appSettings.TaskApiUrl).GetLeftPart(UriPartial.Authority);
            StringBuilder builder = new StringBuilder(baseUrl);
            builder.Append($"/api{_apiVersion}/ip-status.php?action=request&id={taskId}&_t={System.DateTime.Now.Ticks}");
            var body = new Dictionary<string, object>
            {
                ["metrics"] = metrics,
                ["ips"] = ips
            };
            body["host"] = host;
            body["agency"] = _appSettings.ProxyIpUrl;

            var postData = JsonSerializer.Serialize(body);
            using var content = new StringContent(postData, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(builder.ToString(), content, token);
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(token))?.AsObject();
        }
        catch (OperationCanceledException)
        {
            // 请求被取消，安全退出
        }
        catch (Exception ex)
        {
            _logger.LogError($"UpdateProxyIpStatAsync TaskId={taskId} failed: {ex.Message}");
        }
        return null;
    }



    #endregion


}

