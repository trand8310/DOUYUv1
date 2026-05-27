
using MainClient.Common;
using MainClient.Infrastructure;
using MainClient.Logging;
using MainClient.UiTask;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using System.Diagnostics;




namespace MainClient
{
    static class Program
    {
        private static readonly TimeSpan RestartCooldown = TimeSpan.FromMinutes(2);
        private static int _restartRequested;
        private static DateTime _lastRestartRequestUtc = DateTime.MinValue;
        private static PeriodicTimer? _errorDialogTimer;
        private static CancellationTokenSource? _errorDialogCts;

        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        static void Main()
        {
            var appSettings = new AppSettings();
            UserConfigService.Init(appSettings);
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile("appsettings.user.json", optional: true, reloadOnChange: true)
                .Build();
            configuration.GetSection("AppSettings").Bind(appSettings);

            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);

 


            Log.Logger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .MinimumLevel.Information()

                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                .WriteTo.Logger(lc => lc
                    .WriteTo.File(
                        path: Path.Combine(logDir, "app-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 30,
                        shared: true,
                        flushToDiskInterval: TimeSpan.FromSeconds(1),
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                    ))
                .WriteTo.Sink<UiLogSink>()
                .CreateLogger();


            //Log.Logger = new LoggerConfiguration()
            //    .Enrich.FromLogContext()
            //    .Enrich.WithThreadId()
            //    .Enrich.WithProcessId()
            //    .Enrich.WithMachineName()
            //    .MinimumLevel.Information()
            //    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            //    .MinimumLevel.Override("System", LogEventLevel.Warning)
            //    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            //    .WriteTo.Logger(lc => lc
            //        .WriteTo.File(
            //            path: Path.Combine(logDir, "app-.log"),
            //            rollingInterval: RollingInterval.Day,
            //            retainedFileCountLimit: 30,
            //            shared: true,
            //            flushToDiskInterval: TimeSpan.FromSeconds(1),
            //            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
            //        ))
            //    .WriteTo.Logger(lc => lc
            //        .MinimumLevel.Warning()
            //        .WriteTo.Sink(new UiLogSink()))
            //    .CreateLogger();


            var baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var parentDir = Directory.GetParent(baseDir)?.FullName ?? AppDomain.CurrentDomain.BaseDirectory;
            var packagesDir = Path.Combine(parentDir, "packages");
            if (!Directory.Exists(packagesDir))
            {
                Directory.CreateDirectory(packagesDir);
            }


            var builder = new HostBuilder()
                .ConfigureServices((context, services) =>
                {
                    services.Configure<AdeOptions>(opt =>
                    {
                        opt.AppVersion = AppConsts.AppVersion;
                    });

                    services.AddSingleton(appSettings);
                    services.AddHttpClient();
                    //services.AddSingleton<IRootDomainService, RootDomainService>();
                    //services.AddSingleton<IPlaywrightProvider, PlaywrightProvider>();
                    //services.AddSingleton<FileUpdater>(sp =>
                    //{
                    //    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
                    //    var logger = sp.GetRequiredService<ILogger<FileUpdater>>();
                    //    var httpClient = httpClientFactory.CreateClient();

                    //    return new FileUpdater(httpClient, logger);
                    //});

                    //services.AddSingleton<ChineseNameGenerator>();
                    //services.AddSingleton<ChromiumSessionManager>();
                    services.AddSingleton<AdTrafficAggregator>();
                    services.AddSingleton<AdeHelper>();
                    services.AddSingleton<IpHelper>();
                    services.AddSingleton<ProxyTester>();
                    services.AddTransient<MainForm>();

                })
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                })
                .UseSerilog();


            var host = builder.Build();
            CommonHelper.ClearLocalBrowserProcesses();
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (sender, e) =>
            {
                Log.Error(e.Exception, "Application ThreadException");
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                Log.Fatal(e.ExceptionObject as Exception, "UnhandledException");
                RestartApplication();
            };

            AppDomain.CurrentDomain.FirstChanceException += (sender, e) =>
            {
                //Log.Debug(e.Exception, "FirstChanceException");
            };

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Log.Error(e.Exception, "TaskScheduler UnobservedTaskException");
                e.SetObserved();
            };

            StartErrorDialogGuard();

            Application.ApplicationExit += (sender, e) =>
            {
                StopErrorDialogGuard();
                CommonHelper.ClearLocalBrowserProcesses();
            };

            Application.Run(host.Services.GetRequiredService<MainForm>());
        }

        static void RestartApplication()
        {
            return;
            if (Interlocked.Exchange(ref _restartRequested, 1) == 1)
            {
                Log.Warning("RestartApplication skipped: restart already requested.");
                return;
            }

            var utcNow = DateTime.UtcNow;
            var elapsed = utcNow - _lastRestartRequestUtc;
            if (elapsed >= TimeSpan.Zero && elapsed < RestartCooldown)
            {
                Log.Warning("RestartApplication skipped due to cooldown. Elapsed={ElapsedSeconds}s", elapsed.TotalSeconds);
                return;
            }

            _lastRestartRequestUtc = utcNow;

            try
            {
                var exePath = Application.ExecutablePath;
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "restart",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "RestartApplication failed");
            }
            finally
            {

                Environment.Exit(1);
            }
        }

        private static void StartErrorDialogGuard()
        {
            StopErrorDialogGuard();

            _errorDialogCts = new CancellationTokenSource();
            _errorDialogTimer = new PeriodicTimer(TimeSpan.FromSeconds(8));
            var token = _errorDialogCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (await _errorDialogTimer.WaitForNextTickAsync(token))
                    {
                        CommonHelper.ClearAllErrorMsgDialog();
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error dialog guard stopped unexpectedly.");
                }
            }, token);
        }

        private static void StopErrorDialogGuard()
        {
            try
            {
                _errorDialogCts?.Cancel();
            }
            catch
            {
            }

            _errorDialogTimer?.Dispose();
            _errorDialogTimer = null;

            _errorDialogCts?.Dispose();
            _errorDialogCts = null;
        }
    }
}
