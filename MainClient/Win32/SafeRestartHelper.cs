using Serilog;
using System.Diagnostics;


namespace MainClient.Win32
{
    public static class SafeRestartHelper
    {
        public static void RequestSystemRestart(string reason)
        {
            try
            {
                Log.Fatal("准备重启系统。Reason={Reason}", reason);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "shutdown",
                    Arguments = "/r /f /t 5 /c \"系统检测到持续性内存/页面文件压力过高，自动重启\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "触发系统重启失败");
            }
        }

    }
}
