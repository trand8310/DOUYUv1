using System.Text.Json;


namespace MainClient.Infrastructure
{
    public static class UserConfigService
    {
        public static readonly string FilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.user.json");


        public static void Init(AppSettings appSettings)
        {
            if (!File.Exists(UserConfigService.FilePath))
            {
                //appSettings.FetchTaskInterval = 1000;
                //appSettings.MaximumConcurrency = 1;
                //appSettings.MainResetTimeout = 65;
                //appSettings.PageloadedDelay = "8-12";
                //appSettings.PageLoadingTimeout = 20;
                //appSettings.Multiple = 1;
                //appSettings.PVOverride = "1";
                //appSettings.UVOverride = "1";
                //appSettings.UVsTriggerOne = true;
                //appSettings.PVsTriggerOne = true;
                //appSettings.IpTtl = 180;
                //appSettings.DevApiUrl = "http://117.21.200.18:9000/api/fingerprint.php";
                //appSettings.TaskApiUrl = "http://117.21.200.221/client-v5.php";
                //appSettings.IsHiddenMode = true;
                //appSettings.IsProxyMode = true;
                //appSettings.AutoUpdate = false;
            }
        }

        public static void Save<T>(string sectionName, T value)
        {
            Dictionary<string, object> root;
            if (File.Exists(FilePath))
            {
                try
                {
                    root = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(FilePath)) ?? new();
                }
                catch
                {
                    root = new();
                }
            }
            else
            {
                root = new();
            }
            root[sectionName] = value;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

}
