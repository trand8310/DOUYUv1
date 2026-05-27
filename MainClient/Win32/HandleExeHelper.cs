
namespace MainClient.Win32
{
    using System.Diagnostics;
    using System.Text;
    using System.Text.RegularExpressions;

    public sealed class HandleLockInfo
    {
        public string ProcessName { get; set; } = "";
        public int Pid { get; set; }
        public string HandleId { get; set; } = "";
        public string Path { get; set; } = "";
        public string RawLine { get; set; } = "";
    }

    public static class HandleExeHelper
    {
        private static bool IsSafeToKill(string processName)
        {
            string[] safeList =
            {
            "rdpclip.exe",
            "chrome.exe",
            "node.exe",
            "MainClient.exe"
        };

            return safeList.Contains(processName, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsExcludedProcess(string processName, string[]? excludeNames)
        {
            if (excludeNames == null || excludeNames.Length == 0)
                return false;

            string name = Path.GetFileNameWithoutExtension(processName);

            return excludeNames.Any(ex =>
                string.Equals(
                    Path.GetFileNameWithoutExtension(ex),
                    name,
                    StringComparison.OrdinalIgnoreCase));
        }

        public static List<HandleLockInfo> QueryLocks(string handleExePath, string targetPath)
        {
            if (!File.Exists(handleExePath))
                throw new FileNotFoundException("未找到 handle.exe", handleExePath);

            var psi = new ProcessStartInfo
            {
                FileName = handleExePath,
                Arguments = $"\"{targetPath}\" /accepteula",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            string output = (stdout ?? "") + Environment.NewLine + (stderr ?? "");

            var result = new List<HandleLockInfo>();

            // 示例：
            // rdpclip.exe        pid: 47900  type: File            40: C:\Users\Administrator\Desktop\smaide\app
            // explorer.exe       pid: 17364  type: File          1A28: C:\Users\Administrator\Desktop\smaide\app
            var regex = new Regex(
                @"^(?<name>\S+)\s+pid:\s+(?<pid>\d+)\s+type:\s+\S+\s+(?<handle>[0-9A-Fa-f]+):\s+(?<path>.+)$",
                RegexOptions.Multiline);

            foreach (Match m in regex.Matches(output))
            {
                var info = new HandleLockInfo
                {
                    ProcessName = m.Groups["name"].Value.Trim(),
                    Pid = int.Parse(m.Groups["pid"].Value),
                    HandleId = m.Groups["handle"].Value.Trim(),
                    Path = m.Groups["path"].Value.Trim(),
                    RawLine = m.Value
                };

                // 严格按目标路径过滤一次，避免误伤
                if (info.Path.StartsWith(targetPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(info.Path, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(info);
                }
            }

            return result;
        }

        public static void UnlockDirectoryByHandleExe(string handleExePath, string targetPath, string[] excludeNames)
        {
            var locks = QueryLocks(handleExePath, targetPath);

            if (locks.Count == 0)
            {
                Debug.WriteLine($"No locks found for: {targetPath}");
                return;
            }

            // 按 PID + 进程名分组，避免同一个进程多个句柄被重复 Kill
            var groups = locks
                .GroupBy(x => new
                {
                    x.Pid,
                    Name = Path.GetFileNameWithoutExtension(x.ProcessName).ToLowerInvariant()
                })
                .ToList();

            foreach (var group in groups)
            {
                var first = group.First();
                string normalizedName = Path.GetFileNameWithoutExtension(first.ProcessName);

                if (IsExcludedProcess(first.ProcessName, excludeNames))
                {
                    Debug.WriteLine($"Skip excluded process: {first.ProcessName}({first.Pid})");
                    continue;
                }

                // explorer.exe 特殊处理：优先关闭句柄，不直接 Kill
                if (string.Equals(normalizedName, "explorer", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"Process explorer.exe({first.Pid}) has {group.Count()} lock handle(s), try CloseHandle first.");

                    foreach (var item in group)
                    {
                        bool closed = CloseHandleByHandleExe(handleExePath, item.Pid, item.HandleId);
                        Debug.WriteLine($"CloseHandle {item.ProcessName}({item.Pid}) Handle={item.HandleId} Path={item.Path} => {closed}");
                    }

                    continue;
                }

                // 其他白名单进程优先 Kill 一次
                if (IsSafeToKill(first.ProcessName))
                {
                    bool killed = KillProcessByPid(first.Pid);
                    Debug.WriteLine($"Kill {first.ProcessName}({first.Pid}) = {killed}");

                    // Kill 成功后不再逐个关句柄
                    if (killed)
                        continue;

                    // Kill 失败再回退到 CloseHandle
                    foreach (var item in group)
                    {
                        bool closed = CloseHandleByHandleExe(handleExePath, item.Pid, item.HandleId);
                        Debug.WriteLine($"Fallback CloseHandle {item.ProcessName}({item.Pid}) Handle={item.HandleId} Path={item.Path} => {closed}");
                    }

                    continue;
                }

                // 其余进程：逐个 handle 尝试关闭
                foreach (var item in group)
                {
                    bool closed = CloseHandleByHandleExe(handleExePath, item.Pid, item.HandleId);
                    Debug.WriteLine($"CloseHandle {item.ProcessName}({item.Pid}) Handle={item.HandleId} Path={item.Path} => {closed}");
                }
            }
        }

        public static bool CloseHandleByHandleExe(string handleExePath, int pid, string handleId)
        {
            var psi = new ProcessStartInfo
            {
                FileName = handleExePath,
                Arguments = $"-c {handleId} -p {pid} -y /accepteula",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            string text = (stdout ?? "") + Environment.NewLine + (stderr ?? "");
            Debug.WriteLine($"CloseHandleByHandleExe pid={pid}, handle={handleId}, exit={process.ExitCode}, output={text}");

            return process.ExitCode == 0 &&
                   !text.Contains("Error closing handle", StringComparison.OrdinalIgnoreCase);
        }

        public static bool KillProcessByPid(int pid, bool entireProcessTree = true, int waitMs = 3000)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited)
                    return true;

                Debug.WriteLine($"Try kill process: {p.ProcessName}({pid})");

                p.Kill(entireProcessTree);
                bool exited = p.WaitForExit(waitMs);

                Debug.WriteLine($"Kill result: {p.ProcessName}({pid}) => {exited}");
                return exited;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KillProcessByPid failed: pid={pid}, ex={ex}");
                return false;
            }
        }
    }
}
