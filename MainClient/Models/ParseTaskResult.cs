using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MainClient.Models
{
    public sealed class ParseTaskResult
    {
        public bool Success { get; set; }
        public ConsumerTaskContext? Context { get; set; }
    }
}
