using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace MainClient.Ipc
{
    public sealed class PipeEnvelope
    {
        public string Type { get; set; } = "";
        public string? TaskId { get; set; }
        public string? BrowserId { get; set; }
        public bool? Success { get; set; }
        public string? Message { get; set; }
        public JsonNode? Payload { get; set; }
        public JsonNode? Data { get; set; }
    }
}
