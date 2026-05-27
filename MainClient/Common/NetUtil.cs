
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MainClient.Common
{
    public static class NetUtil
    {
        public static List<string> GetPublicIPv4Addresses()
        {
            var result = new List<string>();

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                // 必须启用
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;

                // 排除虚拟/隧道/回环
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                // 必须有网关（否则一般是虚拟或离线网卡）
                var props = ni.GetIPProperties();
                if (!props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(g.Address)))
                    continue;

                foreach (var ua in props.UnicastAddresses)
                {
                    var ip = ua.Address;

                    if (ip.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    if (IsPrivateIPv4(ip))
                        continue;

                    result.Add(ip.ToString());
                }
            }

            return result;
        }

        // 判断是否内网IP
        private static bool IsPrivateIPv4(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();

            return
                b[0] == 10 ||
                (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                (b[0] == 192 && b[1] == 168) ||
                (b[0] == 169 && b[1] == 254) || // APIPA
                b[0] == 127;
        }
    }
}
