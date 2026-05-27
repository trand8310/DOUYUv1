 
    namespace CefClient
    {
        public sealed class CefClientAppContext : ApplicationContext
        {
            private readonly MainForm _mainForm;
            private readonly PipeHostService _pipeHost;
            private readonly CancellationTokenSource _cts = new();

            private int _started;

            public CefClientAppContext(MainForm mainForm, PipeHostService pipeHost)
            {
                _mainForm = mainForm;
                _pipeHost = pipeHost;

                MainForm = _mainForm;

                _mainForm.FormClosed += (_, _) =>
                {
                    try
                    {
                        if (!_cts.IsCancellationRequested)
                            _cts.Cancel();
                    }
                    catch
                    {
                    }

                    ExitThread();
                };
            }

            public void Start()
            {
                if (Interlocked.Exchange(ref _started, 1) != 0)
                    return;

                // 等消息循环起来后再启动
                _mainForm.Shown += MainForm_Shown;
            }

            private void MainForm_Shown(object? sender, EventArgs e)
            {
                _mainForm.Shown -= MainForm_Shown;

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
                        // 这里你接日志
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
                    if (_mainForm.IsHandleCreated && !_mainForm.IsDisposed)
                    {
                        _mainForm.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (!_mainForm.IsDisposed)
                                    _mainForm.Close();
                            }
                            catch
                            {
                                ExitThread();
                            }
                        }));
                    }
                    else
                    {
                        ExitThread();
                    }
                }
                catch
                {
                    ExitThread();
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
 