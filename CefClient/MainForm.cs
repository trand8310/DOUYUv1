using CefClient.Common;
using CefClient.Handler;
using CefSharp;
using CefSharp.WinForms;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CefClient
{
    public partial class MainForm : Form
    {
        private readonly FlowLayoutPanel _hostPanel;
        private readonly ConcurrentDictionary<string, BrowserSlot> _slots = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _createLocks = new();
        private readonly bool _isHiddenMode;

        public MainForm(bool isHiddenMode = false)
        {
            _isHiddenMode = isHiddenMode;
            InitializeComponent();

            if (_isHiddenMode)
            {
                ShowInTaskbar = false;
                WindowState = FormWindowState.Minimized;
                Opacity = 0;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-32000, -32000);
            }
            _hostPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
            };

            Controls.Add(_hostPanel);
        }

        private void MainForm_Load(object sender, EventArgs e)
        {

        }

        protected override void SetVisibleCore(bool value)
        {
            if (_isHiddenMode && !DesignMode)
            {
                value = false;
            }

            base.SetVisibleCore(value);
        }


        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            if (_isHiddenMode)
            {
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed)
                    {
                        Hide();
                    }
                }));
            }
        }

        public async Task<bool> CreateBrowserAsync(
            string taskId,
            string browserId,
            System.Text.Json.Nodes.JsonNode? payload,
            CancellationToken cancellationToken = default)
        {
            var createLock = _createLocks.GetOrAdd(browserId, _ => new SemaphoreSlim(1, 1));
            await createLock.WaitAsync(cancellationToken);

            try
            {
                if (_slots.TryGetValue(browserId, out var existingSlot))
                    return await existingSlot.WaitForInitialLoadAsync(cancellationToken: cancellationToken);

                var slot = await UiInvokeAsync(() =>
                {
                    Directory.CreateDirectory(CefCachePaths.RootCachePath);

                    var cachePath = CefCachePaths.GetBrowserCachePath(browserId);
                    Directory.CreateDirectory(cachePath);

                    var requestContext = new RequestContext(new RequestContextSettings
                    {
                        CachePath = cachePath,
                        PersistUserPreferences = false,
                        PersistSessionCookies = false,
                    });

                    var panel = new Panel
                    {
                        Width = 500,
                        Height = 1000,
                        Margin = new Padding(5),
                        BorderStyle = BorderStyle.FixedSingle
                    };

                    var title = new Label
                    {
                        AutoEllipsis = true,
                        Dock = DockStyle.Top,
                        Height = 28,
                        Text = $"CefClient {browserId}",
                        TextAlign = ContentAlignment.MiddleCenter
                    };
                    var browser = new ChromiumWebBrowser("about:blank", requestContext)
                    {
                        //Dock = DockStyle.Fill,
                        Dock = DockStyle.None,
                        Location = new Point(0, title.Height),
                        Size = new Size(360, 720),
                        //Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                    };
                    browser.DownloadHandler = new DisableDownloadHandler();
                    browser.RequestHandler = new ExternalProtocolRequestHandler(message => { });

                    //browser.FrameLoadStart += (a, b) =>
                    //{
                    //    if (b.Frame.IsMain)
                    //    {
                    //        browser.ShowDevTools();
                    //    }
                    //};
                    panel.Controls.Add(title);
                    panel.Controls.Add(browser);
                    title.SendToBack();
                    _hostPanel.Controls.Add(panel);

                    return new BrowserSlot(browserId, panel, browser, requestContext, cachePath, _hostPanel);
                }, cancellationToken);

                if (!_slots.TryAdd(browserId, slot))
                {
                    await slot.DisposeAsync();
                    return false;
                }

                if (await slot.WaitForInitialLoadAsync(cancellationToken: cancellationToken))
                    return true;

                if (_slots.TryRemove(browserId, out var failedSlot))
                {
                    await failedSlot.DisposeAsync();
                }

                return false;
            }
            finally
            {
                createLock.Release();
            }
        }



        public async Task<BrowserRunResult> RunBrowserAsync(
            string browserId,
            System.Text.Json.Nodes.JsonNode? payload,
            CancellationToken cancellationToken = default,
            Func<BrowserRunStatus, CancellationToken, Task>? statusChanged = null)
        {
            if (!_slots.TryGetValue(browserId, out var slot))
            {
                return new BrowserRunResult
                {
                    BrowserId = browserId,
                    Success = false,
                    Message = "browserId 不存在"
                };
            }

            return await slot.RunAsync(payload, cancellationToken, statusChanged);
        }


        public async Task RemoveBrowserFastAsync(string browserId)
        {
            if (_slots.TryRemove(browserId, out var slot))
            {
                await UiInvokeAsync(() =>
                {
                    if (_hostPanel.Controls.Contains(slot.HostPanel))
                        _hostPanel.Controls.Remove(slot.HostPanel);
                });

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(200);
                        await slot.DisposeHeavyAsync();
                    }
                    catch
                    {
                    }
                });
            }
        }

        public async Task RemoveAllBrowsersAsync()
        {
            foreach (var kv in _slots.ToArray())
            {
                if (_slots.TryRemove(kv.Key, out var slot))
                {
                    await slot.DisposeAsync();
                }
            }
        }

        private Task UiInvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (IsDisposed || Disposing)
            {
                tcs.TrySetException(new ObjectDisposedException(nameof(MainForm)));
                return tcs.Task;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return tcs.Task;
            }

            void Execute()
            {
                try
                {
                    if (IsDisposed || Disposing)
                    {
                        tcs.TrySetException(new ObjectDisposedException(nameof(MainForm)));
                        return;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                        return;
                    }

                    action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }

            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke((Action)Execute);
                }
                else
                {
                    Execute();
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }

            return tcs.Task;
        }

        private Task<T> UiInvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (IsDisposed || Disposing)
            {
                tcs.TrySetException(new ObjectDisposedException(nameof(MainForm)));
                return tcs.Task;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return tcs.Task;
            }

            void Execute()
            {
                try
                {
                    if (IsDisposed || Disposing)
                    {
                        tcs.TrySetException(new ObjectDisposedException(nameof(MainForm)));
                        return;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                        return;
                    }

                    var result = func();
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }

            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke((Action)Execute);
                }
                else
                {
                    Execute();
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }

            return tcs.Task;
        }
        private async Task<bool> TryUiInvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            try
            {
                await UiInvokeAsync(action, cancellationToken);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
