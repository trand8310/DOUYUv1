

namespace MainClient.Common
{
    using System;
    using System.Linq;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public static class RealIpHelper
    {
        private static readonly string[] _ipApiUrls =
        {
            "http://211.154.24.179:9000/api/dash/ipinfo.php",
            "http://117.21.200.221/api/dash/ipinfo.php"
        };

        private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler
        {
            UseProxy = false
        })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        /// <summary>
        /// 并发请求多个 IP 接口，哪个先成功返回就用哪个
        /// </summary>
        public static async Task<string> GetRealIpAsync(CancellationToken cancellationToken = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(6));

            var tasks = _ipApiUrls
                .Select(url => GetIpFromApiAsync(url, cts.Token))
                .ToList();

            while (tasks.Count > 0)
            {
                var finishedTask = await Task.WhenAny(tasks);
                tasks.Remove(finishedTask);

                try
                {
                    var ip = await finishedTask;
                    if (!string.IsNullOrWhiteSpace(ip))
                    {
                        // 有一个成功了，取消其他请求
                        cts.Cancel();
                        return ip;
                    }
                }
                catch
                {
                    // 当前这个接口失败，继续等其他接口
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 从单个接口获取 IP
        /// </summary>
        private static async Task<string> GetIpFromApiAsync(string url, CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
                return string.Empty;

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var data = JsonSerializer.Deserialize<IpInfoResponse>(json, options);

            if (data == null)
                return string.Empty;

            if (!string.Equals(data.Status, "success", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            return data.Query?.Trim() ?? string.Empty;
        }

        private sealed class IpInfoResponse
        {
            public string? Status { get; set; }
            public string? Country { get; set; }
            public string? CountryCode { get; set; }
            public string? Province { get; set; }
            public string? City { get; set; }
            public string? District { get; set; }
            public string? Isp { get; set; }
            public string? Areacode { get; set; }
            public string? Lat { get; set; }
            public string? Lon { get; set; }
            public string? Query { get; set; }
        }
    }
}
