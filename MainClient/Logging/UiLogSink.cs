using Serilog.Core;
using Serilog.Events;

namespace MainClient.Logging
{
    public class UiLogSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            UiLogChannel.Channel.Writer.TryWrite(new UiLogItem
            {
                Time = logEvent.Timestamp.LocalDateTime,
                Level = logEvent.Level,
                Message = logEvent.RenderMessage()
            });
        }
    }
}
