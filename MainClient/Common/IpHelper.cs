using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using MainClient.Extensions;
using MainClient.Infrastructure;


namespace MainClient.Common
{
    public enum IPFormat
    {
        TXT = 1,
        JSON = 2,
    }
    public class IpEntity
    {
        public string value { get; set; } = string.Empty;
        public JsonNode json { get; set; }
        public IPFormat format { get; set; } = IPFormat.TXT;
    }



    public class IpHelper
    {
        private static JsonArray region_1;
        private static JsonArray region_2;
        private static JsonArray region_3;
        private static JsonArray region_4_1;
        private static JsonArray region_4_2;
        private static JsonArray region_ipzan;
        private static JsonArray region_51dail;
        private static JsonArray region_shenlong;

        static string[] delimiters = { "\r", "\n", System.Environment.NewLine };
        static SemaphoreSlim _mutex = new SemaphoreSlim(1);
        static IpHelper()
        {
            region_1 = JsonNode.Parse(Properties.Resources.region_1)?.AsArray() ?? new JsonArray();
            region_2 = JsonNode.Parse(Properties.Resources.region_2)?.AsArray() ?? new JsonArray();
            region_3 = JsonNode.Parse(Properties.Resources.region_3)?.AsArray() ?? new JsonArray();
            region_4_1 = JsonNode.Parse(Properties.Resources.region_4_1)?.AsArray() ?? new JsonArray();
            region_4_2 = JsonNode.Parse(Properties.Resources.region_4_2)?.AsArray() ?? new JsonArray();
            region_ipzan = JsonNode.Parse(Properties.Resources.region_ipzan)?.AsArray() ?? new JsonArray();
            region_51dail = JsonNode.Parse(Properties.Resources.region_51daili)?.AsArray() ?? new JsonArray();
            region_shenlong = JsonNode.Parse(Properties.Resources.region_shenlong)?.AsArray() ?? new JsonArray();
        }

        private static JsonNode? FindMaxCodeNodeByName(JsonArray source, string nameKey, string codeKey, string keyword)
        {
            JsonNode? best = null;
            long bestCode = long.MinValue;

            foreach (var node in source)
            {
                if (node is null)
                    continue;

                var name = node[nameKey]?.ToString();
                if (string.IsNullOrWhiteSpace(name) || !name.Contains(keyword, StringComparison.Ordinal))
                    continue;

                var codeText = node[codeKey]?.ToString();
                if (!long.TryParse(codeText, out var code))
                    continue;

                if (code > bestCode)
                {
                    bestCode = code;
                    best = node;
                }
            }

            return best;
        }

        private static JsonNode? FindCityByName(JsonNode? provinceNode, string cityKeyword)
        {
            if (provinceNode?["mallCityList"] is not JsonArray cityList)
                return null;

            foreach (var city in cityList)
            {
                if (city?["cityName"]?.ToString().Contains(cityKeyword, StringComparison.Ordinal) == true)
                    return city;
            }

            return null;
        }

        private readonly ILogger _logger;
        private readonly AppSettings _appSettings;
        private readonly IHttpClientFactory _httpClientFactory;
        public IpHelper(AppSettings appSettings, IHttpClientFactory httpClientFactory, ILogger<IpHelper> logger)
        {
            _appSettings = appSettings;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }


        private static ConcurrentQueue<IpEntity> ipQueues = new ConcurrentQueue<IpEntity>();
        public async Task<IpEntity> GetProxyIpAsync(JsonNode task, int count = 0)
        {
            if (ipQueues.TryDequeue(out var value))
            {
                return value;
            }
            IPFormat iPFormat = IPFormat.TXT;
            var url = GetIpUrl(task, out iPFormat, count);
            var client = _httpClientFactory.CreateClient("IP_DATA");
            try
            {
                await _mutex.WaitAsync();
                HttpResponseMessage response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(content) && !(content.Contains("白名单") || content.Contains("暂无") || content.Contains("没有") || content.Contains("过多") || content.Contains("请重试")))
                    {
                        if (iPFormat == IPFormat.TXT)
                        {
                            var values = content.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var text in values)
                            {
                                ipQueues.Enqueue(new IpEntity() { format = iPFormat, value = text });
                            }
                        }
                        else if (iPFormat == IPFormat.JSON)
                        {
                            var json = JsonNode.Parse(content)?.AsObject();
                            if (url.Contains("service.ipzan.com"))
                            {
                                foreach (var data in json.SelectToken("data.list").Children())
                                {
                                    ipQueues.Enqueue(new IpEntity() { format = iPFormat, json = data });
                                }
                            }
                            else
                            {
                                foreach (var data in json.SelectToken("data").Children())
                                {
                                    ipQueues.Enqueue(new IpEntity() { format = iPFormat, json = data });
                                }
                            }

                        }

                        if (ipQueues.TryDequeue(out var entity))
                        {
                            return entity;
                        }
                    }
                    throw new Exception(content);

                }
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message);
                //_logger.LogError($"GetIpUrl => {url},{ex.InnerException?.Message}");
            }
            finally
            {
                _mutex.Release();
            }
            return null;
        }





        private string GetIpUrl(JsonNode task, out IPFormat format, int count = 0)
        {
            format = IPFormat.TXT;
            var url = _appSettings.ProxyIpUrl.Trim();
            try
            {
                //四川[18]:成都[188],
                var query = System.Web.HttpUtility.ParseQueryString(url);

                if (url.Contains("shenlongip.com"))
                {
                    #region shenlongip.com
                    //http://api.shenlongip.com/ip?key=pjr1xjh4&area=310100,320100,320200,320300,320400,320500,320600,320700,320800,320900,321000,321100,321200,321300&protocol=1&mr=1&pattern=txt&count=1&sign=e207c36f5687a57e9802c8190f428ea4
                    //if (query["format"] != null && query["format"].ToString().Equals("json"))
                    //    format = IPFormat.JSON;
                    //else
                    //    format = IPFormat.TXT;

                    //http://api.shenlongip.com/ip?key=k902nyta&protocol=1&mr=2&pattern=json&count=1&sign=e207c36f5687a57e9802c8190f428ea4&rip=1


                    if (query["pattern"] != null && query["pattern"].ToString().Equals("json"))
                        format = IPFormat.JSON;
                    else
                        format = IPFormat.TXT;


                    if (_appSettings.IsRealIp)
                    {
                        format = IPFormat.JSON;

                        if (Regex.IsMatch(url, @"pattern=\w+"))
                            url = Regex.Replace(url, @"pattern=\w+", $"pattern=json");
                        else
                            url = url += $"&pattern=json";


                        if (Regex.IsMatch(url, @"rip=\d+"))
                            url = Regex.Replace(url, @"rip=\d+", $"rip=1");
                        else
                            url = url += $"&rip=1";
                    }

                    if (count > 1)
                    {
                        if (Regex.IsMatch(url, @"count=[\d]*"))
                            url = Regex.Replace(url, @"count=[\d]*", $"count={count}");
                        else
                            url = url += $"&count={count}";
                    }

                    if (task["address"] != null && !string.IsNullOrEmpty(task["address"].ToString()) && !task["address"].ToString().Equals("全部"))
                    {
                        var address_list = task["address"].ToString().Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries);
                        List<string> area_codes = new List<string>();
                        foreach (var address in address_list)
                        {
                            if (address.Contains(":"))
                            {
                                var address_values = address.Split(':');
                                var m1 = Regex.Match(address_values[0], @"\w+");
                                if (m1.Success)
                                {
                                    var m2 = Regex.Match(address_values[1], @"\w+");
                                    if (m2.Success)
                                    {
                                        var areas = region_shenlong.Where(w => w["name"].ToString().Contains(m2.Value)).ToList();
                                        if (areas != null && areas.Count() > 0)
                                        {
                                            var area_code = string.Join(",", areas.Select(s => s["code"].ToString()));
                                            area_codes.Add(area_code);
                                        }
                                    }
                                    else
                                    {
                                        var areas = region_shenlong.Where(w => w["name"].ToString().Contains(m1.Value)).ToList();
                                        if (areas != null && areas.Count() > 0)
                                        {
                                            var area_code = string.Join(",", areas.Select(s => s["code"].ToString()));
                                            area_codes.Add(area_code);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                var m1 = Regex.Match(address, @"\w+");
                                if (m1.Success)
                                {
                                    var areas = region_shenlong.Where(w => w["name"].ToString().Contains(m1.Value));
                                    if (areas != null && areas.Count() > 0)
                                    {
                                        var area_code = string.Join(",", areas.Select(s => s["code"].ToString()));
                                        area_codes.Add(area_code);
                                    }
                                }
                            }
                        }


                        if (area_codes.Count() > 0)
                        {
                            var all_area_code = string.Join(",", area_codes);
                            if (Regex.IsMatch(url, @"area=[\d,]+"))
                                url = Regex.Replace(url, @"area=[\d,]+", $"area={all_area_code}");
                            else
                                url = url += $"&area={all_area_code}";
                        }

                    }
                    #endregion
                }
                else if (url.Contains("51daili.com"))
                {
                    #region 51daili.com
                    //http://bapi.51daili.com/traffic/getip?linePoolIndex=1&packid=12&time=2&qty=12&port=1&format=txt&usertype=17&uid=39905


                    if (count > 1)
                    {
                        if (Regex.IsMatch(url, @"qty=[\d]*"))
                            url = Regex.Replace(url, @"qty=[\d]*", $"qty={count}");
                        else
                            url = url += $"&qty={count}";
                    }


                    if (task["address"] != null && !string.IsNullOrEmpty(task["address"].ToString()) && !task["address"].ToString().Equals("全部"))
                    {
                        var address_list = task["address"].ToString().Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries);
                        var address = address_list[Math.Abs(Guid.NewGuid().GetHashCode()) % address_list.Length].Split(':');
                        if (address.Length > 1)
                        {
                            var m1 = Regex.Match(address[0], @"\w+");
                            if (m1.Success)
                            {
                                var area_prov = FindMaxCodeNodeByName(region_51dail, "provinceName", "provinceCode", m1.Value);
                                if (area_prov != null)
                                {
                                    var m2 = Regex.Match(address[1], @"\w+");
                                    if (m2.Success)
                                    {
                                        var area_city = FindCityByName(area_prov, m2.Value);
                                        if (area_city != null)
                                        {
                                            if (Regex.IsMatch(url, @"regionCode=[\w]*[^&]?"))
                                                url = Regex.Replace(url, @"regionCode=[\w]*[^&]?", $"regionCode={area_city["cityCode"]}");
                                            else
                                                url = url += $"&regionCode={area_city["cityCode"]}";
                                        }
                                        else
                                        {
                                            if (Regex.IsMatch(url, @"regionCode=[\w]*[^&]?"))
                                                url = Regex.Replace(url, @"area=[\w]*[^&]?", $"regionCode={area_prov["provinceCode"]}");
                                            else
                                                url = url += $"&regionCode={area_prov["provinceCode"]}";
                                        }
                                    }
                                    else
                                    {
                                        if (Regex.IsMatch(url, @"regionCode=[\w]*[^&]?"))
                                            url = Regex.Replace(url, @"area=[\w]*[^&]?", $"regionCode={area_prov["provinceCode"]}");
                                        else
                                            url = url += $"&regionCode={area_prov["provinceCode"]}";
                                    }
                                }
                            }
                        }
                        else
                        {
                            var m1 = Regex.Match(address[0], @"\w+");
                            if (m1.Success)
                            {
                                var area_prov = FindMaxCodeNodeByName(region_51dail, "provinceName", "provinceCode", m1.Value);
                                if (area_prov != null)
                                {
                                    if (Regex.IsMatch(url, @"regionCode=[\w]*[^&]?"))
                                        url = Regex.Replace(url, @"regionCode=[\w]*[^&]?", $"regionCode={area_prov["provinceCode"]}");
                                    else
                                        url = url += $"&regionCode={area_prov["provinceCode"]}";
                                }
                            }
                        }
                    }
                    #endregion

                }
                else if (url.Contains("service.ipzan.com"))
                {
                    #region service.ipzan.com
                    //http://service.ipzan.com/core-extract?num=1&no=20211030082718667537&minute=3&format=json&repeat=1&protocol=1&pool=quality&mode=whitelist&secret=u5ta45tj

                    if (_appSettings.IsRealIp)
                    {
                        format = IPFormat.JSON;
                        //realIp=1
                        if (Regex.IsMatch(url, @"format=\w+"))
                            url = Regex.Replace(url, @"format=\w+", $"format=json");
                        else
                            url = url += $"&format=json";


                        if (Regex.IsMatch(url, @"realIp=\d+"))
                            url = Regex.Replace(url, @"realIp=\d+", $"realIp=1");
                        else
                            url = url += $"&realIp=1";
                    }
                    else
                    {
                        if (Regex.IsMatch(url, @"format=\w+"))
                            url = Regex.Replace(url, @"format=\w+", $"format=txt");
                    }


                    if (count > 1)
                    {
                        if (Regex.IsMatch(url, @"num=[\d]*"))
                            url = Regex.Replace(url, @"num=[\d]*", $"num={count}");
                        else
                            url = url += $"&num={count}";
                    }

                    if (task["address"] != null && !string.IsNullOrEmpty(task["address"].ToString()) && !task["address"].ToString().Equals("全部"))
                    {
                        var addrs = task["address"].ToString().Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries);
                        var address = addrs[Math.Abs(Guid.NewGuid().GetHashCode()) % addrs.Length].Split(':');
                        var area_addr = string.Empty;
                        string area = string.Empty;
                        if (address.Length > 1)
                        {
                            var m1 = Regex.Match(address[1], @"\w+");
                            if (m1.Success)
                            {
                                var area_res = FindMaxCodeNodeByName(region_ipzan, "name", "code", m1.Value);
                                if (area_res == null)
                                {
                                    m1 = Regex.Match(address[0], @"\w+");
                                    if (m1.Success)
                                    {
                                        area_res = FindMaxCodeNodeByName(region_ipzan, "name", "code", m1.Value);
                                    }
                                }
                                if (area_res != null)
                                {
                                    if (Regex.IsMatch(url, @"area=[\w]*[^&]?"))
                                        url = Regex.Replace(url, @"area=[\w]*[^&]?", $"area={area_res["code"]}");
                                    else
                                        url = url += $"&area={area_res["code"]}";
                                }
                            }
                        }
                        else
                        {
                            var m1 = Regex.Match(address[0], @"\w+");
                            if (m1.Success)
                            {
                                var area_res = FindMaxCodeNodeByName(region_ipzan, "name", "code", m1.Value);
                                if (area_res != null)
                                {
                                    if (Regex.IsMatch(url, @"area=[\w]*[^&]?"))
                                        url = Regex.Replace(url, @"area=[\w]*[^&]?", $"area={area_res["code"]}");
                                    else
                                        url = url += $"&area={area_res["code"]}";
                                }
                            }
                        }
                    }
                    #endregion

                }


                else if (url.Contains("api.test.myipproxy.com") || url.Contains("api.hailiangip.com") || url.Contains("111.73.45.100") || url.Contains("47.97.20.179"))
                {
                    //http://api.test.myipproxy.com:8422/api/getIp?type=1&num=1&orderId=O21081016192288073951&time=1628583680&sign=95d2880db7a7effe459df80ee80ba249&unbindTime=180&dataType=1&pid=&cid=
                    #region myipproxy & hailiangip & ...

                    if (query["dataType"] != null && Int32.TryParse(query["dataType"].ToString(), out int dataType) && dataType > 0)
                        format = IPFormat.TXT;
                    else
                        format = IPFormat.JSON;

                    if (count > 0)
                    {
                        if (Regex.IsMatch(url, @"num=[\d]*"))
                            url = Regex.Replace(url, @"num=[\d]*", $"num={count}");
                        else
                            url = url += $"&num={count}";
                    }

                    if (_appSettings.IsRealIp)
                    {
                        format = IPFormat.JSON;
                        if (Regex.IsMatch(url, @"dataType=[\d]*"))
                            url = Regex.Replace(url, @"dataType=[\d]*", $"dataType=0");
                        else
                            url = url += $"&dataType=0";
                    }
                    else
                    {
                        if (Regex.IsMatch(url, @"dataType=[\d]*"))
                            url = Regex.Replace(url, @"dataType=[\d]*", $"dataType=1");
                        else
                            url = url += $"&dataType=1";
                    }

                    if (task["address"] != null && !string.IsNullOrEmpty(task["address"].ToString()) && !task["address"].ToString().Equals("全部"))
                    {
                        var addrs = task["address"].ToString().Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries);
                        var address = addrs[Math.Abs(Guid.NewGuid().GetHashCode()) % addrs.Length].Split(':');
                        var m1 = Regex.Match(address[0], @"\d+");
                        string pid = string.Empty, cid;
                        if (m1.Success)
                        {
                            pid = m1.Value;
                            if (Regex.IsMatch(url, @"pid=[\d]*"))
                                url = Regex.Replace(url, @"pid=[\d]*", $"pid={pid}");
                            else
                                url = url += $"&pid={pid}";
                        }
                        if (address.Count() > 1)
                        {
                            var m2 = Regex.Match(address[1], @"\d+");
                            if (m2.Success)
                            {
                                cid = m2.Value;
                                if (!string.IsNullOrWhiteSpace(pid) && !pid.Equals(cid))
                                {
                                    if (Regex.IsMatch(url, @"cid=[\d]*"))
                                        url = Regex.Replace(url, @"cid=[\d]*", $"cid={cid}");
                                    else
                                        url = url += $"&cid={cid}";
                                }
                            }
                        }
                    }

                    #endregion
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
            return url;
        }





        public async Task<bool> PingIP(string proxy_server)
        {
            Ping pingSender = new Ping();
            PingOptions options = new PingOptions();
            options.DontFragment = true;
            string data = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            byte[] buffer = Encoding.ASCII.GetBytes(data);
            int timeout = 1000;
            PingReply reply = await pingSender.SendPingAsync(proxy_server, timeout, buffer, options);
            if (reply.Status == IPStatus.Success)
            {
                return true;
            }
            return false;
        }

        public async Task<string> GetIpInfo(string proxy)
        {
            HttpClientHandler httpClientHandler = new HttpClientHandler() { Proxy = new WebProxy(proxy, BypassOnLocal: false), UseProxy = true };
            using (var client = new HttpClient(httpClientHandler))
            {
                try
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    HttpResponseMessage response = await client.GetAsync("http://ip-api.com/json/?lang=zh-CN");

                    if (response.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        return await response.Content.ReadAsStringAsync();
                    }
                }
                catch (WebException ex)
                {
                    Debug.WriteLine(ex.Message);

                }
                return null;
            }
            ;
        }

        public async Task<string> GetIpInfo()
        {

            using (var client = new HttpClient())
            {
                try
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    HttpResponseMessage response = await client.GetAsync("http://ip-api.com/json/?lang=zh-CN");

                    if (response.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        return await response.Content.ReadAsStringAsync();
                    }
                }
                catch (WebException ex)
                {
                    Debug.WriteLine(ex.Message);

                }
                return null;
            }
            ;
        }
        //http://ip-api.com/json/?lang=zh-CN


    }
}
