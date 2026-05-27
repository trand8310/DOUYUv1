using CefClient.Common;
using CefSharp;
using CefSharp.WinForms;

namespace CefClient
{
    public class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
 

            var pipeName = args
            .FirstOrDefault(x => x.StartsWith("--pipe-name=", StringComparison.OrdinalIgnoreCase))
            ?.Substring("--pipe-name=".Length);

            var consumerId = args
            .FirstOrDefault(x => x.StartsWith("--consumer-id=", StringComparison.OrdinalIgnoreCase))
            ?.Substring("--consumer-id=".Length);

            var isHiddenModeArg = args
            .FirstOrDefault(x => x.StartsWith("--hidden-mode=", StringComparison.OrdinalIgnoreCase))
            ?.Substring("--hidden-mode=".Length);

            var isHiddenMode = bool.TryParse(isHiddenModeArg, out var hiddenMode) && hiddenMode;

            if (!string.IsNullOrWhiteSpace(consumerId))
            {
                CefCachePaths.RootCachePath = CefCachePaths.GetConsumerRootCachePath(consumerId);
            }

            ApplicationConfiguration.Initialize();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.ThreadException += (sender, e) =>
            {
                // TODO: 这里接你的日志
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                // TODO: 这里接你的日志
            };

            AppDomain.CurrentDomain.FirstChanceException += (sender, e) =>
            {
                // 如无必要，不建议这里做重日志
            };

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                e.SetObserved();
            };
  
            CefSharpSettings.SubprocessExitIfParentProcessClosed = true;
            Cef.EnableWaitForBrowsersToClose();
           
            var settings = new CefSettings
            {
                RootCachePath = CefCachePaths.RootCachePath,
                PersistSessionCookies = false,
                PersistUserPreferences = false,
                IgnoreCertificateErrors = true,
                UserAgent = "Mozilla/5.0 (Linux; Android 13; SM-G981B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Mobile Safari/537.36",
            };
            settings.CefCommandLineArgs.Add("enable-media-stream");
            settings.CefCommandLineArgs.Add("use-fake-ui-for-media-stream");
            settings.CefCommandLineArgs.Add("enable-usermedia-screen-capturing");

            Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);

            Application.ApplicationExit += (sender, e) =>
            {
                if (Cef.IsInitialized)
                {
                    Cef.WaitForBrowsersToClose();
                    Cef.Shutdown();
                }
            };

            var mainForm = new MainForm(isHiddenMode);
            // 带管道参数：由主进程调度
            if (!string.IsNullOrWhiteSpace(pipeName))
            {
                var pipeHost = new PipeHostService(pipeName, mainForm);
                var appContext = new CefClientAppContext(mainForm, pipeHost);

                appContext.Start();
                Application.Run(appContext);
                return 0;
            }

            // 不带管道参数：本地直接调试运行
            Application.Run(mainForm);
            return 0;
        }
    }
}