namespace CefClient.Handler
{

    using CefSharp;
    using CefSharp.Handler;

    public sealed class DisableDownloadHandler : DownloadHandler
    {
        protected override bool CanDownload(
               IWebBrowser chromiumWebBrowser,
               IBrowser browser,
               string url,
               string requestMethod)
        {
            return false;
        }

        protected override bool OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem, IBeforeDownloadCallback callback)
        {
            using (callback)
            {
                // 不调用 callback.Continue(...)
                // 下载不会继续
            }

            // true 表示这个下载事件你已经处理了
            return true;
        }

        protected override void OnDownloadUpdated(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem, IDownloadItemCallback callback)
        {
            if (callback != null && !callback.IsDisposed)
            {
                if (!downloadItem.IsCancelled && !downloadItem.IsComplete)
                {
                    callback.Cancel();
                }
            }
        }
    }
}
