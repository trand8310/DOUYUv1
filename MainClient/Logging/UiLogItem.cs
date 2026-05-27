using Serilog.Events;

namespace MainClient.Logging
{
    public class UiLogItem
    {
        public DateTime Time { get; set; }
        public LogEventLevel Level { get; set; }
        public string Message { get; set; }
    }
}
