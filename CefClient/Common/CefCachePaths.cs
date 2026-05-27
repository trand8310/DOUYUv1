using System.Threading.Tasks;

namespace CefClient.Common
{
    internal static class CefCachePaths
    {
        public static string RootCachePath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "User Data");

        public static string GetConsumerRootCachePath(string consumerId)
        {
            return Path.Combine(RootCachePath, "Consumers", consumerId);
        }
        public static string GetBrowserCachePath(string browserId)
        {
            return Path.Combine(RootCachePath, browserId);
        }

        private static string SafeSegment(string value)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var chars = value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
            var safeValue = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(safeValue) ? "unknown" : safeValue;
        }
    }
}
