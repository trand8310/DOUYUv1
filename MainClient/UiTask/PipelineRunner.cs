using System.Threading.Channels;

namespace MainClient.UiTask
{
    public class PipelineRunner<T>
    {
        private readonly Channel<T> _channel;
        private readonly Func<ChannelWriter<T>, CancellationToken, Task> _producer;
        private readonly Func<int, T, CancellationToken, Task> _consumer;
        private readonly int _consumerCount;

        public event Action<long>? ProgressChanged;
        public event Action? Started;
        public event Action? Completed;
        public event Action? Canceled;
        public event Action<Exception>? Faulted;

        public PipelineRunner(
            int capacity,
            int consumerCount,
            Func<ChannelWriter<T>, CancellationToken, Task> producer,
            Func<int, T, CancellationToken, Task> consumer)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (consumerCount <= 0) throw new ArgumentOutOfRangeException(nameof(consumerCount));

            _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = false
            });

            _consumerCount = consumerCount;
            _producer = producer ?? throw new ArgumentNullException(nameof(producer));
            _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        }

        public async Task RunAsync(CancellationToken token)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var runToken = linkedCts.Token;

            Started?.Invoke();

            long globalItemNumber = 0;

            var producerTask = Task.Run(() => _producer(_channel.Writer, runToken), runToken);

            var consumerTasks = Enumerable.Range(0, _consumerCount)
                .Select(consumerId => Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var item in _channel.Reader.ReadAllAsync(runToken).ConfigureAwait(false))
                        {
                            if (runToken.IsCancellationRequested)
                                break;

                            try
                            {
                                await _consumer(consumerId, item, runToken).ConfigureAwait(false);

                                var itemNumber = Interlocked.Increment(ref globalItemNumber);
                                ProgressChanged?.Invoke(itemNumber);
                            }
                            catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                            {
                                break;
                            }
                            catch (Exception ex)
                            {
                                Faulted?.Invoke(ex);
                                // 单条任务失败，不中断整体
                            }
                        }
                    }
                    catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                    {
                        // 正常停止
                    }
                }, runToken))
                .ToArray();

            try
            {
                await producerTask.ConfigureAwait(false);
                await Task.WhenAll(consumerTasks).ConfigureAwait(false);

                if (token.IsCancellationRequested)
                {
                    Canceled?.Invoke();
                    return;
                }

                Completed?.Invoke();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Canceled?.Invoke();
            }
            catch (Exception ex)
            {
                linkedCts.Cancel();
                Faulted?.Invoke(ex);
                throw;
            }
        }
    }
}
