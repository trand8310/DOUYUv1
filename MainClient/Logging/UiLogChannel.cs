using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MainClient.Logging
{
    public static class UiLogChannel
    {
        public static readonly Channel<UiLogItem> Channel =
            System.Threading.Channels.Channel.CreateBounded<UiLogItem>(
                new BoundedChannelOptions(10000)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest // 关键
                });
    }
}
