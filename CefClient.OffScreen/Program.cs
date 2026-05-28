using CefClient.Common;
using CefSharp;


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


            var dpiScale = args
            .FirstOrDefault(x => x.StartsWith("--dpi-scale=", StringComparison.OrdinalIgnoreCase))
            ?.Substring("--dpi-scale=".Length);
 

            if (string.IsNullOrWhiteSpace(pipeName))
            {
                // OffScreen 项目只作为管道子进程运行；未提供 pipe-name 时不创建任何窗体，直接退出。
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(consumerId))
            {
                CefCachePaths.RootCachePath = CefCachePaths.GetConsumerRootCachePath(consumerId);
            }
            var defaultSubprocessPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CefSharp.BrowserSubprocess.exe");

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
            var settings = new CefSharp.OffScreen.CefSettings
            {

                BrowserSubprocessPath = defaultSubprocessPath,
                RootCachePath = CefCachePaths.RootCachePath,
                //CachePath = null,
                PersistSessionCookies = false,
                PersistUserPreferences= false,
                WindowlessRenderingEnabled = true,
                IgnoreCertificateErrors=true,
                UserAgent= "Mozilla/5.0 (Linux; Android 13; SM-G981B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Mobile Safari/537.36",
            };
            settings.CefCommandLineArgs.Add("enable-media-stream");
            settings.CefCommandLineArgs.Add("use-fake-ui-for-media-stream");
            settings.CefCommandLineArgs.Add("enable-usermedia-screen-capturing");

            ///--disable-chrome-runtime
            /////incognito
            //settings.CefCommandLineArgs.Add("disable-chrome-runtime");
            //settings.CefCommandLineArgs.Add("incognito");


            //settings.CefCommandLineArgs.Add("disable-extensions", "1");
            //settings.CefCommandLineArgs.Add("disable-plugins", "1");
            //settings.CefCommandLineArgs.Add("disable-pdf-extension", "1");
            //settings.CefCommandLineArgs.Add("disable-print-preview", "1");
            //settings.CefCommandLineArgs.Add("disable-notifications", "1");
            //settings.CefCommandLineArgs.Add("disable-speech-api", "1");
            //settings.CefCommandLineArgs.Add("disable-background-networking", "1");
            //settings.CefCommandLineArgs.Add("disable-sync", "1");
            //settings.CefCommandLineArgs.Add("metrics-recording-only", "1");
            //settings.CefCommandLineArgs.Add("disable-default-apps", "1");
            //settings.CefCommandLineArgs.Add("no-first-run", "1");
            //settings.CefCommandLineArgs.Add("no-default-browser-check", "1");

            //settings.CefCommandLineArgs.Add("disable-gpu", "1");
            //settings.CefCommandLineArgs.Add("disable-gpu-compositing", "1");
            //settings.CefCommandLineArgs.Add("disable-gpu-shader-disk-cache", "1");
            //settings.CefCommandLineArgs.Add("disable-webgl", "1");
            //settings.CefCommandLineArgs.Add("disable-webgpu", "1");
            //settings.CefCommandLineArgs.Add("disable-component-update", "1");
            //settings.CefCommandLineArgs.Add("disable-gpu-vsync");
            //settings.CefCommandLineArgs.Add("disable-features", "Widevine");

            //settings.CefCommandLineArgs.Add("disk-cache-size", (100 * 1024 * 1024).ToString());   // 100MB
            //settings.CefCommandLineArgs.Add("media-cache-size", (50 * 1024 * 1024).ToString());   // 50MB

            //settings.DisableGpuAcceleration();
            settings.SetOffScreenRenderingBestPerformanceArgs();
 

            Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);

            Application.ApplicationExit += (sender, e) =>
            {
                if (Cef.IsInitialized)
                {
                    Cef.WaitForBrowsersToClose();
                    Cef.Shutdown();
                }
            };

            // 由主进程调度。OSR 子进程不再创建本地预览窗体，截图通过管道回传给 MainClient。
            var browserHost = new OffScreenBrowserHost();
            var pipeHost = new PipeHostService(pipeName, browserHost);
            var appContext = new CefClientAppContext(pipeHost);

            appContext.Start();
            Application.Run(appContext);
            return 0;
        }
    }
}