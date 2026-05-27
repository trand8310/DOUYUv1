using System.Text.Json.Nodes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MainClient.Models
{
    public sealed class ConsumerTaskContext
    {
        public string UniqueId { get; set; }
        public int TaskId { get; set; }
        public int TotalUV { get; set; }
        public int TotalPV { get; set; }
        public string DevClientId { get; set; } = "0";
        public OSType OS { get; set; }

        public string? ProxyServer { get; set; }
        public string RealIp { get; set; } = string.Empty;
        public JsonObject? IpInfo { get; set; }

        public DateTime StartTime { get; set; } = DateTime.Now;
        public string TaskTitle { get; set; } = string.Empty;
    }
}
