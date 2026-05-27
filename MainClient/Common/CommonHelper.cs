using MainClient.Win32;
using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;




namespace MainClient.Common
{
    public class CommonHelper
    {
        private const string Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

        /// <summary>
        /// 生成一个随机数
        /// </summary>
        /// <returns></returns>
        public static uint RandomNumber()
        {
            byte[] bytes = new byte[4];
            RandomNumberGenerator.Fill(bytes);
            uint value = BitConverter.ToUInt32(bytes, 0);
            return value;
        }
        public static string GenerateRandomText(int length)
        {
            if (length < 1)
                throw new ArgumentException("Length must be greater than 0", nameof(length));

            var result = new StringBuilder(length);
            var data = new byte[length];

            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(data);
            }
            for (int i = 0; i < length; i++)
            {
                var index = data[i] % Chars.Length;
                result.Append(Chars[index]);
            }
            return result.ToString();
        }

        public static TimeSpan GetRandomizedInterval(int minutes, int maxRandomSeconds)
        {
            return TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(Random.Shared.Next(-180, 180));
        }


        /// <summary>
        /// 随机生成一个满足百分比的数字
        /// </summary>
        /// <param name="probability"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public static bool IsEventOccurring(double probability)
        {
            if (probability < 0 || probability > 1)
                throw new ArgumentOutOfRangeException(nameof(probability), "Probability must be between 0 and 1");
            double randomValue = Random.Shared.NextDouble();
            return randomValue < probability;
        }


        public static string HmacSha1Sign(byte[] input, byte[] key)
        {
            HMACSHA1 myhmacsha1 = new HMACSHA1(key);
            MemoryStream stream = new MemoryStream(input);
            return myhmacsha1.ComputeHash(stream).Aggregate("", (s, e) => s + String.Format("{0:x2}", e), s => s);
        }

        public static string ComputeSha1Hash(string input)
        {
            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] inputBytes = Encoding.ASCII.GetBytes(input);
                byte[] hashBytes = sha1.ComputeHash(inputBytes);
                StringBuilder sb = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        public static long UnixTimeNow()
        {
            return new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
        }
        public static long UnixTimeNowSecond()
        {
            return new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
        }

        public static string CreateMD5(string input)
        {
            using (var md5 = MD5.Create())
            {
                var result = md5.ComputeHash(Encoding.ASCII.GetBytes(input));
                var strResult = BitConverter.ToString(result);
                return strResult.Replace("-", "").ToLower();
            }
        }

        public static string MD5Hash(string input)
        {
            using (var md5 = MD5.Create())
            {
                var result = md5.ComputeHash(Encoding.ASCII.GetBytes(input));
                var strResult = BitConverter.ToString(result);
                return strResult.Replace("-", "");
            }
        }

        public static int RandomRange(int min, int max)
        {
            return Random.Shared.Next(min, max);
        }

        /// <summary>
        /// 返回[min, max)之间的随机整数
        /// </summary>
        public static int NextInt(int min, int max)
        {
            return Random.Shared.Next(min, max);
        }
        public static Int64 NextInt64(Int64 min, Int64 max)
        {
            return Random.Shared.NextInt64(min, max);
        }


        public static double NextDouble()
        {
            return Random.Shared.NextDouble();
        }

        public static double NextDouble(double min, double max)
        {
            return min + Random.Shared.NextDouble() * (max - min);
        }

        public static Int16 Get16BitHash(string s)
        {
            return (Int16)(s.GetHashCode() & 0xFFFF);
        }

        public static string ComputeHash(string input)
        {
            byte[] bytes = Encoding.Default.GetBytes(input);
            var iSHA = SHA1.Create();
            bytes = iSHA.ComputeHash(bytes);
            StringBuilder buf = new StringBuilder();
            foreach (byte b in bytes)
            {
                buf.AppendFormat("{0:x2}", b);
            }
            return buf.ToString().ToUpper();
        }



 




        public static void CopyFilesRecursively(DirectoryInfo source, DirectoryInfo target)
        {
            foreach (DirectoryInfo dir in source.GetDirectories())
            {
                CopyFilesRecursively(dir, target.CreateSubdirectory(dir.Name));
            }

            foreach (FileInfo file in source.GetFiles())
            {
                file.CopyTo(Path.Combine(target.FullName, file.Name), true);
            }
        }


        /// <summary>  
        /// 根据GUID获取16位的唯一字符串  
        /// </summary>  
        /// <param name=\"guid\"></param>  
        /// <returns></returns>  
        public static string GuidTo16String()
        {
            long i = 1;
            foreach (byte b in Guid.NewGuid().ToByteArray())
                i *= ((int)b + 1);
            return string.Format("{0:x}", i - DateTime.Now.Ticks);
        }
        /// <summary>  
        /// 根据GUID获取19位的唯一数字序列  
        /// </summary>  
        /// <returns></returns>  
        public static long GuidToLongID()
        {
            byte[] buffer = Guid.NewGuid().ToByteArray();
            return BitConverter.ToInt64(buffer, 0);
        }
        public static string GetRandomWifiMacAddress()
        {
            var random = new Random();
            var buffer = new byte[6];
            random.NextBytes(buffer);
            buffer[0] = 02;
            var result = string.Concat(buffer.Select(x => string.Format("{0}", x.ToString("X2"))).ToArray());
            return result.ToUpper().Insert(2, "-");
        }
        public static string GetRandomMacAddress()
        {
            var random = new Random();
            var buffer = new byte[6];
            random.NextBytes(buffer);
            var result = String.Concat(buffer.Select(x => string.Format("{0}:", x.ToString("X2"))).ToArray());
            return result.TrimEnd(':');
        }


        public static int GetOS(string userAgent)
        {
            var tmp = userAgent.ToLower();
            if (tmp.Contains("android"))
                return 0;//Android
            else if (tmp.ToLower().Contains("windows phone"))
                return 2;//Windows Phone
            else if (tmp.Contains("iphone") || tmp.Contains("ipad"))
                return 1;//Iphone
            return 3;
        }

        public static void ClearProcesses(string[] processNames, string baseDir = null)
        {
            if (processNames.Count() > 0)
            {
                var Processes = Process.GetProcesses().Where(w => processNames.Contains(w.ProcessName));
                foreach (Process item in Processes)
                {
                    if (!item.HasExited)
                    {
                        try
                        {
                            item.Kill();
                        }
                        catch (Exception ex)
                        {
                            KillProcExec(item.Id);
                            Debug.WriteLine(ex.Message);
                        }

                    }
                }
            }


        }


        public static Process ExecCmd()
        {
            Process p = null;
            try
            {
                p = new Process();
                p.StartInfo.FileName = "cmd.exe";
                p.StartInfo.UseShellExecute = false;        //是否使用操作系统shell启动
                p.StartInfo.RedirectStandardInput = true;   //接受来自调用程序的输入信息
                p.StartInfo.RedirectStandardOutput = true;  //由调用程序获取输出信息
                p.StartInfo.RedirectStandardError = true;   //重定向标准错误输出
                p.StartInfo.CreateNoWindow = true;          //不显示程序窗口
            }
            catch (Exception)
            {
                throw;
            }
            return p;
        }
        public static void KillProcExec(int procId)
        {
            string cmd = string.Format("taskkill /f /t /im {0}", procId); //强制结束指定进程
            Process ps = null;
            try
            {
                ps = ExecCmd();
                ps.Start();
                ps.StandardInput.WriteLine(cmd + "&exit");
            }
            catch
            {

            }
            finally
            {
                ps.Close();
            }
        }


        public static long IpToInt(string ip)
        {
            string[] items = ip.Split('.');
            return long.Parse(items[0]) << 24
                    | long.Parse(items[1]) << 16
                    | long.Parse(items[2]) << 8
                    | long.Parse(items[3]);
        }



        public static void DeleteDownloadDir(string targetDir, string[] extensions)
        {
            if (!Directory.Exists(targetDir))
                return;

            foreach (var file in Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    // 判断文件扩展名是否在指定的扩展名数组中
                    if (extensions.Contains(Path.GetExtension(file).ToLower()))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                        Console.WriteLine($"删除文件: {file}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"删除失败: {file} - {ex.Message}");
                }
            }
        }


        public static void DeleteTempDir(string targetDir)
        {
            if (!Directory.Exists(targetDir))
                return;

            Regex numberDirRegex = new Regex(@"^\d+$");
            foreach (var dir in Directory.GetDirectories(targetDir))
            {
                string dirName = Path.GetFileName(dir);
                // 只处理纯数字目录
                if (!numberDirRegex.IsMatch(dirName))
                    continue;
                try
                {
                    // 1. 删除该目录下所有文件
                    foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                        }
                        catch { /* 忽略被占用文件 */ }
                    }
                    // 2. 删除该目录下所有子目录（不删除 dir 本身）
                    foreach (var subDir in Directory.GetDirectories(dir))
                    {
                        try
                        {
                            Directory.Delete(subDir, true);
                        }
                        catch { }
                    }
                    Console.WriteLine($"已清空目录：{dir}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"清空失败：{dir}，原因：{ex.Message}");
                }
            }
        }


        static List<string> GetTopLevelPlaywrightDirs(string rootPath, string prefix)
        {
            var result = new List<string>();
            try
            {
                foreach (var dir in Directory.GetDirectories(rootPath))
                {
                    string dirName = Path.GetFileName(dir);

                    if (dirName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(dir);
                    }
                    else
                    {
                        result.AddRange(GetTopLevelPlaywrightDirs(dir, prefix));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"无法访问目录 {rootPath}: {ex.Message}");
            }
            return result;
        }
        public static void DeletePlaywrightDirs(string tempPath, string prefix = "playwright-")
        {
            if (!Directory.Exists(tempPath))
                return;
            try
            {
                var dirsToDelete = GetTopLevelPlaywrightDirs(tempPath, prefix);
                foreach (var dir in dirsToDelete)
                {
                    try
                    {
                        Directory.Delete(dir, true);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 清空目录内容（不删除根目录）
        /// </summary>
        /// <param name="path"></param>
        public static void ClearDirectory(string path)
        {
            if (!Directory.Exists(path))
                return;

            try
            {
                // 删除所有文件
                foreach (var file in Directory.GetFiles(path))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                    }
                    catch
                    {
                        // 可记录日志
                    }
                }

                // 删除所有子目录
                foreach (var dir in Directory.GetDirectories(path))
                {
                    try
                    {
                        RemoveDirectoryRecursive(dir);
                    }
                    catch
                    {
                        // 可记录日志
                    }
                }
            }
            catch
            {
                // 整体异常处理
            }
        }

        /// <summary>
        /// 删除目录（强力递归）
        /// </summary>
        /// <param name="dir"></param>
        private static void RemoveDirectoryRecursive(string dir)
        {
            if (!Directory.Exists(dir))
                return;

            // 清除只读属性
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch { }
            }

            Directory.Delete(dir, true);
        }



        public static void DeleteCookieFile(string dirRoot)
        {
            try
            {
                string[] rootDirs = Directory.GetDirectories(dirRoot);
                string[] rootFiles = Directory.GetFiles(dirRoot);
                foreach (string s2 in rootFiles)
                {
                    if (s2.Contains("Cookies"))
                    {
                        File.Delete(s2);
                    }
                }
                foreach (string s1 in rootDirs)
                {
                    DeleteCookieFile(s1);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message.ToString());
            }
        }




        public static void ClearAllErrorMsgDialog()
        {
            string[] allTitles = [
                "node.exe - 应用程序错误",
                "WerFault.exe - 应用程序错误",
                "chrome.exe - 应用程序错误",
                "chrome.exe - 系统错误",
            ];
            // 枚举所有窗口
            NativeMethod.EnumWindows((hWnd, lParam) =>
            {
                string title = NativeMethod.GetWindowTitle(hWnd);
                if (allTitles.Contains(title))
                {
                    NativeMethod.SendMessage(hWnd, NativeMethod.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
                return true; // 继续枚举下一个窗口
            }, IntPtr.Zero);
        }

        public static void ClearErrorMsgDialog(string title)
        {
            try
            {
                var _wndRes = NativeMethod.FindWindowByCaption(IntPtr.Zero, title);
                if (_wndRes != IntPtr.Zero)
                {
                    NativeMethod.SendMessage(_wndRes, NativeMethod.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }

        }

        public static void ClearCacheFile()
        {
            #region 删除物理文件
            ////for (int parallelIndex = 1; parallelIndex <= setting.MaximumParallel; parallelIndex++)
            ////{
            ////    try
            ////    {
            ////        Directory.Delete(System.IO.Path.Combine(System.AppDomain.CurrentDomain.SetupInformation.ApplicationBase, "chrome", "User Data", parallelIndex.ToString()), recursive: true);
            ////    }
            ////    catch (Exception ex)
            ////    {
            ////        Console.WriteLine(ex.Message);
            ////    }
            ////    try
            ////    {
            ////        CommonHelper.DeleteCookieFile(System.IO.Path.Combine(System.AppDomain.CurrentDomain.SetupInformation.ApplicationBase, "chrome", "User Data", parallelIndex.ToString()));
            ////    }
            ////    catch (Exception ex)
            ////    {
            ////        Console.WriteLine(ex.Message);
            ////    }
            ////}
            #endregion
        }



        public static void ClearCacheFile(int processIndex)
        {
            #region 删除物理文件

            try
            {
                string cachePath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.SetupInformation.ApplicationBase, "chrome", "User Data", processIndex.ToString());
                if (System.IO.Directory.Exists(cachePath))
                    Directory.Delete(cachePath, recursive: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            #endregion
        }

        /// <summary>
        /// 注释: 清除所有Chrome和ChromeDriver进程
        /// </summary>
        public static void KillAllChromeProcess()
        {
            try
            {
                List<Process> list = new List<Process>();
                list.AddRange(Process.GetProcessesByName("chrome").Where(w => w.MainModule.FileName.StartsWith(AppDomain.CurrentDomain.BaseDirectory)));
                foreach (var process in list)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {


                    }
                }
            }
            catch (Exception)
            {


            }

        }


        public static void ClearLocalChromeProcesses()
        {
            ClearLocalBrowserProcesses();
        }

        /// <summary>
        /// 清理残留的 CefClient/CefClient.OffScreen 进程，以及当前程序目录下的浏览器辅助进程。
        /// </summary>
        public static void ClearLocalBrowserProcesses()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                string[] localOnlyTargets = { "chrome.exe", "node.exe" };
                string[] cefTargets = { "CefClient.exe", "CefClient.OffScreen.exe" };

                using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, Name, ExecutablePath FROM Win32_Process"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        try
                        {
                            string name = obj["Name"]?.ToString() ?? string.Empty;
                            string path = obj["ExecutablePath"]?.ToString() ?? string.Empty;

                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path))
                                continue;

                            var isCefClientProcess = cefTargets.Contains(name, StringComparer.OrdinalIgnoreCase);
                            var isLocalSupportProcess =
                                localOnlyTargets.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                                path.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase);

                            if (!isCefClientProcess && !isLocalSupportProcess)
                                continue;

                            int pid = Convert.ToInt32(obj["ProcessId"]);
                            if (pid == Environment.ProcessId)
                                continue;

                            KillProcessTree(pid);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception)
            {
            }

        }

        private static void KillProcessTree(int pid)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    proc.Kill(true);
                    proc.WaitForExit(3000);
                }
            }
            catch
            {
                try
                {
                    using var taskKill = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/F /T /PID {pid}",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });

                    taskKill?.WaitForExit(3000);
                }
                catch { }
            }
        }


        public static void DeleteDirectoryWithRetry(string dir, int retryCount, int delayMs, bool ignoreFailure = false)
        {
            if (!Directory.Exists(dir))
                return;

            Exception? lastEx = null;

            for (int i = 1; i <= retryCount; i++)
            {
                try
                {
                    RemoveReadOnlyAttributes(dir);
                    Directory.Delete(dir, true);
                    return;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    Thread.Sleep(delayMs);
                }
            }

            if (!ignoreFailure)
                throw new IOException($"删除目录失败: {dir}", lastEx);
        }

        public static void RemoveReadOnlyAttributes(string dir)
        {
            if (!Directory.Exists(dir))
                return;

            foreach (var path in Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attr = File.GetAttributes(path);
                    if ((attr & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
                    }
                }
                catch
                {
                }
            }

            try
            {
                var attr = File.GetAttributes(dir);
                if ((attr & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(dir, attr & ~FileAttributes.ReadOnly);
                }
            }
            catch
            {
            }
        }



        public static void RunMemReductAndWait()
        {
            string exePath = Path.Combine(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName!, "memreduct", "memreduct.exe");

            if (!File.Exists(exePath))
                return;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "/cleanup",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var p = Process.Start(psi);
            p?.WaitForExit(TimeSpan.FromSeconds(30)); // 最多等待5秒
        }

        public static void EmptyStandbyList()
        {
            string exePath = Path.Combine(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName!, "sysinternals", "EmptyStandbyList.exe");

            if (!File.Exists(exePath))
                return;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "standbylist",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var p = Process.Start(psi);
            p?.WaitForExit(TimeSpan.FromSeconds(5)); // 最多等待5秒
        }


        //EmptyStandbyList.exe standbylist




        #region  Ip操作

        private static readonly string[] _ipApiUrls =
        {
            "http://211.154.24.179:9000/api/dash/ipinfo.php",
            "http://117.21.200.18:9000/api/dash/ipinfo.php",
            "http://117.21.200.221/api/dash/ipinfo.php",
            "http://ip-api.com/json/?lang=zh-CN",
            "https://ipinfo.io/json",
        };

        private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler
        {
            UseProxy = false
        })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        /// <summary>
        /// 判断是否内网IP
        /// </summary>
        /// <param name="ip"></param>
        /// <returns></returns>
        private static bool IsPrivateIPv4(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();

            return
                b[0] == 10 ||
                (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                (b[0] == 192 && b[1] == 168) ||
                (b[0] == 169 && b[1] == 254) || // APIPA
                b[0] == 127;
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

        /// <summary>
        /// 并发请求多个 IP 接口，哪个先成功返回就用哪个
        /// </summary>
        private static async Task<string> GetRealIpAsync(CancellationToken cancellationToken = default)
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
        /// 获取本机网卡的IP地址
        /// </summary>
        /// <returns></returns>
        private static List<string> GetPublicIPv4Addresses()
        {
            var result = new List<string>();

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                // 必须启用
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;

                // 排除虚拟/隧道/回环
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                // 必须有网关（否则一般是虚拟或离线网卡）
                var props = ni.GetIPProperties();
                if (!props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(g.Address)))
                    continue;

                foreach (var ua in props.UnicastAddresses)
                {
                    var ip = ua.Address;

                    if (ip.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    if (IsPrivateIPv4(ip))
                        continue;

                    result.Add(ip.ToString());
                }
            }

            return result;
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

        private static string? _hostCache;
        private static readonly SemaphoreSlim _host_lock = new(1, 1);
        public static async Task<string> GetLocalHostAsync()
        {
            // 快速路径（无锁）
            if (!string.IsNullOrWhiteSpace(_hostCache))
                return _hostCache;
            await _host_lock.WaitAsync();
            try
            {
                // 双重检查
                if (!string.IsNullOrWhiteSpace(_hostCache))
                    return _hostCache;
                // ① 先尝试本机公网 IPv4
                try
                {
                    var localIp = GetPublicIPv4Addresses().FirstOrDefault();

                    if (!string.IsNullOrWhiteSpace(localIp))
                    {
                        _hostCache = localIp;
                        return _hostCache;
                    }
                }
                catch { }
                // ② 请求外部接口获取公网 IP
                try
                {
                    var realIp = await GetRealIpAsync();
                    if (!string.IsNullOrWhiteSpace(realIp))
                    {
                        _hostCache = realIp;
                        return _hostCache;
                    }
                }
                catch { }
                // ③ 最终兜底
                _hostCache = "";
                return _hostCache;
            }
            finally
            {
                _host_lock.Release();
            }
        }



        #endregion


    }
}
