namespace CefClient
{
    public sealed class CefClientAppContext : ApplicationContext
    {
        private readonly PipeHostService _pipeHost;
        private readonly CancellationTokenSource _cts = new();
        private int _started;

        public CefClientAppContext(PipeHostService pipeHost)
        {
            _pipeHost = pipeHost;
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;

            ThreadPool.QueueUserWorkItem(_ => StartPipeLoop());
        }

        private void StartPipeLoop()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _pipeHost.StartAsync(_cts.Token);
                    await _pipeHost.RunLoopAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
                finally
                {
                    SafeExit();
                }
            });
        }

        private void SafeExit()
        {
            try
            {
                if (!_cts.IsCancellationRequested)
                    _cts.Cancel();
            }
            catch
            {
            }

            try
            {
                ExitThread();
            }
            catch
            {
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _cts.Cancel();
                }
                catch
                {
                }

                try
                {
                    _pipeHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                }

                _cts.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
