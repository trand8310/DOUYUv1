using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace CefClient
{
    public sealed class OffScreenBrowserHost
    {
        private readonly ConcurrentDictionary<string, BrowserSlot> _slots = new();

        public event Action<string>? BrowserLog;
        public event Action<string, byte[]>? BrowserScreenshot;
        public event Func<string, BrowserRunStatus, CancellationToken, Task>? BrowserStatus;

        public Task<bool> CreateBrowserAsync(string browserId, CancellationToken cancellationToken = default)
        {
            // OSR 模式不预创建浏览器；每次 RunBrowserAsync 都会创建一次性 BrowserSlot 并在 RunAsync 内释放。
            return Task.FromResult(true);
        }

        public async Task<BrowserRunResult> RunBrowserAsync(
            string browserId,
            JsonNode? payload,
            CancellationToken cancellationToken = default)
        {
            var slot = new BrowserSlot(browserId, HandleBrowserScreenshot, WriteBrowserLog, PublishBrowserStatusAsync);
            return await slot.RunAsync(payload, cancellationToken);
        }

        public async Task RemoveBrowserFastAsync(string browserId)
        {
            if (_slots.TryRemove(browserId, out var slot))
            {
                await slot.DetachFromUiAsync();

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
            var disposeTasks = new List<Task>();
            foreach (var kv in _slots.ToArray())
            {
                if (_slots.TryRemove(kv.Key, out var slot))
                {
                    disposeTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await slot.DisposeHeavyAsync();
                        }
                        catch
                        {
                        }
                    }));
                }
            }

            if (disposeTasks.Count == 0)
                return;

            var all = Task.WhenAll(disposeTasks);
            var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(2)));
            if (finished == all)
                await all;
        }

        private void HandleBrowserScreenshot(string browserId, byte[] screenshotBytes)
        {
            BrowserScreenshot?.Invoke(browserId, screenshotBytes);
        }

        private void WriteBrowserLog(string message)
        {
            BrowserLog?.Invoke(message);
        }

        private Task PublishBrowserStatusAsync(BrowserRunStatus status, CancellationToken cancellationToken)
        {
            return BrowserStatus?.Invoke(status.BrowserId, status, cancellationToken) ?? Task.CompletedTask;
        }
    }
}
