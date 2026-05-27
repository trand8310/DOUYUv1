namespace CefClient.Common
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    public enum DeviceSystemType
    {
        Android = 1,
        IOS = 2,
        Windows = 3
    }

    public enum DeviceKind
    {
        Unknown = 0,
        Phone = 1,
        Tablet = 2,
        Desktop = 3
    }

    public sealed class DeviceViewportResult
    {
        public DeviceSystemType SystemType { get; set; }
        public DeviceKind Kind { get; set; }

        public string ProfileName { get; set; }

        // 物理分辨率
        public int ScreenWidth { get; set; }
        public int ScreenHeight { get; set; }

        // 浏览器 viewport CSS 尺寸
        public int ViewportWidth { get; set; }
        public int ViewportHeight { get; set; }

        public float DeviceScaleFactor { get; set; }

        public double DprX { get; set; }
        public double DprY { get; set; }

        public bool Mobile { get; set; }

        public bool IsExactModelMatch { get; set; }
        public bool IsExactResolutionMatch { get; set; }
        public bool IsFallback { get; set; }

        public double Score { get; set; }

        public override string ToString()
        {
            return
                "System=" + SystemType +
                ", Kind=" + Kind +
                ", Profile=" + ProfileName +
                ", Screen=" + ScreenWidth + "x" + ScreenHeight +
                ", Viewport=" + ViewportWidth + "x" + ViewportHeight +
                ", DPR=" + DeviceScaleFactor.ToString("F3") +
                ", DprX=" + DprX.ToString("F4") +
                ", DprY=" + DprY.ToString("F4") +
                ", Mobile=" + Mobile +
                ", ExactModel=" + IsExactModelMatch +
                ", ExactResolution=" + IsExactResolutionMatch +
                ", Fallback=" + IsFallback +
                ", Score=" + Score.ToString("F6");
        }
    }

    internal sealed class DeviceProfile
    {
        public DeviceSystemType SystemType;
        public DeviceKind Kind;
        public string Name;

        public int ScreenWidth;
        public int ScreenHeight;

        public int ViewportWidth;
        public int ViewportHeight;

        public float DeviceScaleFactor;

        public DeviceProfile(
            DeviceSystemType systemType,
            DeviceKind kind,
            string name,
            int screenWidth,
            int screenHeight,
            int viewportWidth,
            int viewportHeight,
            float deviceScaleFactor)
        {
            SystemType = systemType;
            Kind = kind;
            Name = name;
            ScreenWidth = screenWidth;
            ScreenHeight = screenHeight;
            ViewportWidth = viewportWidth;
            ViewportHeight = viewportHeight;
            DeviceScaleFactor = deviceScaleFactor;
        }
    }

    public static class DeviceViewportMatcher
    {
        private static readonly List<DeviceProfile> IOSProfiles = new List<DeviceProfile>();
        private static readonly Dictionary<string, DeviceProfile> IOSModelMap =
            new Dictionary<string, DeviceProfile>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<DeviceProfile> AndroidProfiles = new List<DeviceProfile>();
        private static readonly List<DeviceProfile> WindowsProfiles = new List<DeviceProfile>();

        static DeviceViewportMatcher()
        {
            InitIOS();
            InitAndroid();
            InitWindows();
        }

        /// <summary>
        /// 统一入口。
        /// sw/sh 必传。
        /// modelName 可选。
        /// systemType 必传。
        /// </summary>
        public static DeviceViewportResult Match(
            int sw,
            int sh,
            DeviceSystemType systemType,
            string? modelName = null)
        {
            if (sw <= 0 || sh <= 0)
                throw new ArgumentOutOfRangeException("sw/sh", "sw 和 sh 必须大于 0");

            if (systemType == DeviceSystemType.IOS)
            {
                if (!string.IsNullOrWhiteSpace(modelName))
                {
                    DeviceViewportResult byName = MatchIOSByName(sw, sh, modelName);
                    if (byName != null)
                        return byName;
                }

                return MatchByResolution(sw, sh, IOSProfiles, systemType);
            }

            if (systemType == DeviceSystemType.Android)
            {
                return MatchByResolution(sw, sh, AndroidProfiles, systemType);
            }

            if (systemType == DeviceSystemType.Windows)
            {
                return MatchByResolution(sw, sh, WindowsProfiles, systemType);
            }

            return BuildFallback(sw, sh, systemType);
        }

        public static DeviceViewportResult Match(
            int sw,
            int sh,
            DeviceSystemType systemType)
        {
            return Match(sw, sh, systemType, null);
        }

        private static DeviceViewportResult MatchIOSByName(int sw, int sh, string modelName)
        {
            string key = NormalizeName(modelName);

            DeviceProfile profile;
            if (!IOSModelMap.TryGetValue(key, out profile))
                return null;

            DeviceViewportResult result = BuildResult(profile, sw, sh);
            result.IsExactModelMatch = true;

            int inputW = Math.Min(sw, sh);
            int inputH = Math.Max(sw, sh);

            result.IsExactResolutionMatch =
                profile.ScreenWidth == inputW &&
                profile.ScreenHeight == inputH;

            // iOS 有机型名时，以机型数据为准
            result.ScreenWidth = profile.ScreenWidth;
            result.ScreenHeight = profile.ScreenHeight;
            result.ViewportWidth = profile.ViewportWidth;
            result.ViewportHeight = profile.ViewportHeight;
            result.DeviceScaleFactor = profile.DeviceScaleFactor;
            result.DprX = (double)profile.ScreenWidth / profile.ViewportWidth;
            result.DprY = (double)profile.ScreenHeight / profile.ViewportHeight;

            return result;
        }

        private static DeviceViewportResult MatchByResolution(
            int sw,
            int sh,
            List<DeviceProfile> profiles,
            DeviceSystemType systemType)
        {
            if (profiles == null || profiles.Count == 0)
                return BuildFallback(sw, sh, systemType);

            int inputW;
            int inputH;

            if (systemType == DeviceSystemType.Windows)
            {
                inputW = Math.Max(sw, sh);
                inputH = Math.Min(sw, sh);
            }
            else
            {
                inputW = Math.Min(sw, sh);
                inputH = Math.Max(sw, sh);
            }

            DeviceViewportResult best = null;

            for (int i = 0; i < profiles.Count; i++)
            {
                DeviceProfile p = profiles[i];

                double score = GetResolutionScore(inputW, inputH, p);

                DeviceViewportResult result = BuildResult(p, inputW, inputH);
                result.Score = score;

                result.IsExactResolutionMatch =
                    p.ScreenWidth == inputW &&
                    p.ScreenHeight == inputH;

                if (result.IsExactResolutionMatch)
                {
                    result.Score = 0.0;
                    return result;
                }

                if (best == null || result.Score < best.Score)
                    best = result;
            }

            if (best != null)
                return best;

            return BuildFallback(sw, sh, systemType);
        }

        private static double GetResolutionScore(int inputW, int inputH, DeviceProfile p)
        {
            double widthDiff = Math.Abs((double)inputW - p.ScreenWidth) / p.ScreenWidth;
            double heightDiff = Math.Abs((double)inputH - p.ScreenHeight) / p.ScreenHeight;

            double inputRatio = (double)inputH / inputW;
            double profileRatio = (double)p.ScreenHeight / p.ScreenWidth;
            double ratioDiff = Math.Abs(inputRatio - profileRatio);

            double score = 0.0;
            score += widthDiff * 10.0;
            score += heightDiff * 10.0;
            score += ratioDiff * 5.0;

            return score;
        }

        private static DeviceViewportResult BuildResult(DeviceProfile p, int inputScreenWidth, int inputScreenHeight)
        {
            double dprX = (double)p.ScreenWidth / p.ViewportWidth;
            double dprY = (double)p.ScreenHeight / p.ViewportHeight;

            return new DeviceViewportResult
            {
                SystemType = p.SystemType,
                Kind = p.Kind,
                ProfileName = p.Name,

                ScreenWidth = p.ScreenWidth,
                ScreenHeight = p.ScreenHeight,

                ViewportWidth = p.ViewportWidth,
                ViewportHeight = p.ViewportHeight,

                DeviceScaleFactor = p.DeviceScaleFactor,

                DprX = dprX,
                DprY = dprY,

                Mobile = p.Kind == DeviceKind.Phone || p.Kind == DeviceKind.Tablet,

                IsExactModelMatch = false,
                IsExactResolutionMatch =
                    p.ScreenWidth == inputScreenWidth &&
                    p.ScreenHeight == inputScreenHeight,

                IsFallback = false,
                Score = 0.0
            };
        }

        private static DeviceViewportResult BuildFallback(
            int sw,
            int sh,
            DeviceSystemType systemType)
        {
            int screenW;
            int screenH;

            if (systemType == DeviceSystemType.Windows)
            {
                screenW = Math.Max(sw, sh);
                screenH = Math.Min(sw, sh);
            }
            else
            {
                screenW = Math.Min(sw, sh);
                screenH = Math.Max(sw, sh);
            }

            if (systemType == DeviceSystemType.IOS)
            {
                float dpr = screenW <= 900 ? 2.0f : 3.0f;

                return new DeviceViewportResult
                {
                    SystemType = DeviceSystemType.IOS,
                    Kind = DeviceKind.Phone,
                    ProfileName = "iOS Fallback",

                    ScreenWidth = screenW,
                    ScreenHeight = screenH,

                    ViewportWidth = (int)Math.Round(screenW / dpr, MidpointRounding.AwayFromZero),
                    ViewportHeight = (int)Math.Round(screenH / dpr, MidpointRounding.AwayFromZero),

                    DeviceScaleFactor = dpr,

                    DprX = dpr,
                    DprY = dpr,

                    Mobile = true,

                    IsFallback = true,
                    Score = 9999.0
                };
            }

            if (systemType == DeviceSystemType.Android)
            {
                float dpr = GuessAndroidDpr(screenW);
                int vw = (int)Math.Round(screenW / dpr, MidpointRounding.AwayFromZero);
                int vh = (int)Math.Round(screenH / dpr, MidpointRounding.AwayFromZero);

                return new DeviceViewportResult
                {
                    SystemType = DeviceSystemType.Android,
                    Kind = DeviceKind.Phone,
                    ProfileName = "Android Fallback",

                    ScreenWidth = screenW,
                    ScreenHeight = screenH,

                    ViewportWidth = vw,
                    ViewportHeight = vh,

                    DeviceScaleFactor = dpr,

                    DprX = (double)screenW / vw,
                    DprY = (double)screenH / vh,

                    Mobile = true,

                    IsFallback = true,
                    Score = 9999.0
                };
            }

            // Windows fallback
            float windowsDpr = GuessWindowsDpr(screenW, screenH);
            int cssW = (int)Math.Round(screenW / windowsDpr, MidpointRounding.AwayFromZero);
            int cssH = (int)Math.Round(screenH / windowsDpr, MidpointRounding.AwayFromZero);

            return new DeviceViewportResult
            {
                SystemType = DeviceSystemType.Windows,
                Kind = DeviceKind.Desktop,
                ProfileName = "Windows Fallback",

                ScreenWidth = screenW,
                ScreenHeight = screenH,

                ViewportWidth = cssW,
                ViewportHeight = cssH,

                DeviceScaleFactor = windowsDpr,

                DprX = (double)screenW / cssW,
                DprY = (double)screenH / cssH,

                Mobile = false,

                IsFallback = true,
                Score = 9999.0
            };
        }

        private static float GuessAndroidDpr(int screenWidth)
        {
            if (screenWidth <= 720)
                return 2.0f;

            if (screenWidth <= 900)
                return 2.25f;

            if (screenWidth <= 1080)
                return 3.0f;

            if (screenWidth <= 1440)
                return 3.5f;

            return 4.0f;
        }

        private static float GuessWindowsDpr(int screenWidth, int screenHeight)
        {
            if (screenWidth >= 3840)
                return 2.0f;

            if (screenWidth >= 2560)
                return 1.25f;

            return 1.0f;
        }

        private static string NormalizeName(string name)
        {
            if (name == null)
                return string.Empty;

            string value = name.Trim().ToLowerInvariant();
            value = value.Replace("_", "");
            value = value.Replace("-", "");
            value = value.Replace(" ", "");

            value = Regex.Replace(value, @"[^a-z0-9]", "");

            return value;
        }

        private static void AddIOS(
            string name,
            int sw,
            int sh,
            int vw,
            int vh,
            float dpr)
        {
            DeviceProfile p = new DeviceProfile(
                DeviceSystemType.IOS,
                DeviceKind.Phone,
                name,
                Math.Min(sw, sh),
                Math.Max(sw, sh),
                vw,
                vh,
                dpr);

            IOSProfiles.Add(p);

            string key = NormalizeName(name);

            if (!IOSModelMap.ContainsKey(key))
                IOSModelMap.Add(key, p);
            else
                IOSModelMap[key] = p;
        }

        private static void AddAndroid(
            string name,
            int sw,
            int sh,
            int vw,
            int vh,
            float dpr)
        {
            DeviceProfile p = new DeviceProfile(
                DeviceSystemType.Android,
                DeviceKind.Phone,
                name,
                Math.Min(sw, sh),
                Math.Max(sw, sh),
                vw,
                vh,
                dpr);

            AndroidProfiles.Add(p);
        }

        private static void AddAndroidTablet(
            string name,
            int sw,
            int sh,
            int vw,
            int vh,
            float dpr)
        {
            DeviceProfile p = new DeviceProfile(
                DeviceSystemType.Android,
                DeviceKind.Tablet,
                name,
                Math.Min(sw, sh),
                Math.Max(sw, sh),
                vw,
                vh,
                dpr);

            AndroidProfiles.Add(p);
        }

        private static void AddWindows(
            string name,
            int sw,
            int sh,
            int vw,
            int vh,
            float dpr)
        {
            DeviceProfile p = new DeviceProfile(
                DeviceSystemType.Windows,
                DeviceKind.Desktop,
                name,
                Math.Max(sw, sh),
                Math.Min(sw, sh),
                vw,
                vh,
                dpr);

            WindowsProfiles.Add(p);
        }

        private static void InitIOS()
        {
            AddIOS("iPhone 17e", 1170, 2532, 390, 844, 3.0f);
            AddIOS("iPhone 17 Pro Max", 1320, 2868, 440, 956, 3.0f);
            AddIOS("iPhone 17 Pro", 1206, 2622, 402, 874, 3.0f);
            AddIOS("iPhone Air", 1260, 2736, 420, 912, 3.0f);
            AddIOS("iPhone 17", 1206, 2622, 402, 874, 3.0f);

            AddIOS("iPhone 16e", 1170, 2532, 390, 844, 3.0f);
            AddIOS("iPhone 16 Pro Max", 1320, 2868, 440, 956, 3.0f);
            AddIOS("iPhone 16 Pro", 1206, 2622, 402, 874, 3.0f);
            AddIOS("iPhone 16 Plus", 1290, 2796, 430, 932, 3.0f);
            AddIOS("iPhone 16", 1179, 2556, 393, 852, 3.0f);

            AddIOS("iPhone 15", 1179, 2556, 393, 852, 3.0f);
            AddIOS("iPhone 15 Plus", 1290, 2796, 430, 932, 3.0f);
            AddIOS("iPhone 15 Pro", 1179, 2556, 393, 852, 3.0f);
            AddIOS("iPhone 15 Pro Max", 1290, 2796, 430, 932, 3.0f);

            AddIOS("iPhone 14", 1170, 2532, 390, 844, 3.0f);
            AddIOS("iPhone 14 Plus", 1284, 2778, 428, 926, 3.0f);
            AddIOS("iPhone 14 Pro", 1179, 2556, 393, 852, 3.0f);
            AddIOS("iPhone 14 Pro Max", 1290, 2796, 430, 932, 3.0f);

            AddIOS("iPhone 13", 1170, 2532, 390, 844, 3.0f);
            AddIOS("iPhone 13 Pro", 1170, 2532, 390, 844, 3.0f);
            AddIOS("iPhone 13 Pro Max", 1284, 2778, 428, 926, 3.0f);
            AddIOS("iPhone 13 mini", 1080, 2340, 360, 780, 3.0f);

            AddIOS("iPhone 12", 1170, 2532, 390, 844, 3.0f);
            AddIOS("iPhone 12 Pro", 1170, 2532, 390, 844, 3.0f);
            AddIOS("iPhone 12 Pro Max", 1284, 2778, 428, 926, 3.0f);
            AddIOS("iPhone 12 mini", 1080, 2340, 360, 780, 3.0f);

            AddIOS("iPhone 11", 828, 1792, 414, 896, 2.0f);
            AddIOS("iPhone 11 Pro", 1125, 2436, 375, 812, 3.0f);
            AddIOS("iPhone 11 Pro Max", 1242, 2688, 414, 896, 3.0f);
        }

        private static void InitAndroid()
        {
            // Android 常见手机档位
            AddAndroid("Android 720x1280 DPR2", 720, 1280, 360, 640, 2.0f);
            AddAndroid("Android 720x1440 DPR2", 720, 1440, 360, 720, 2.0f);
            AddAndroid("Android 720x1480 DPR2", 720, 1480, 360, 740, 2.0f);
            AddAndroid("Android 720x1520 DPR2", 720, 1520, 360, 760, 2.0f);
            AddAndroid("Android 720x1600 DPR2", 720, 1600, 360, 800, 2.0f);

            AddAndroid("Android 1080x1920 DPR3", 1080, 1920, 360, 640, 3.0f);
            AddAndroid("Android 1080x2160 DPR3", 1080, 2160, 360, 720, 3.0f);
            AddAndroid("Android 1080x2220 DPR3", 1080, 2220, 360, 740, 3.0f);
            AddAndroid("Android 1080x2280 DPR3", 1080, 2280, 360, 760, 3.0f);
            AddAndroid("Android 1080x2340 DPR3", 1080, 2340, 360, 780, 3.0f);
            AddAndroid("Android 1080x2400 DPR3", 1080, 2400, 360, 800, 3.0f);

            AddAndroid("Android 1170x2532 DPR3", 1170, 2532, 390, 844, 3.0f);
            AddAndroid("Android 1080x2400 393x873", 1080, 2400, 393, 873, 2.75f);
            AddAndroid("Android 1440x2960 DPR4", 1440, 2960, 360, 740, 4.0f);
            AddAndroid("Android 1440x3040 DPR4", 1440, 3040, 360, 760, 4.0f);
            AddAndroid("Android 1440x3200 DPR4", 1440, 3200, 360, 800, 4.0f);

            // Android 平板
            AddAndroidTablet("Android Tablet 1200x1920 DPR2", 1200, 1920, 600, 960, 2.0f);
            AddAndroidTablet("Android Tablet 1600x2560 DPR2", 1600, 2560, 800, 1280, 2.0f);
            AddAndroidTablet("Android Tablet 2000x2800 DPR2.5", 2000, 2800, 800, 1120, 2.5f);
        }

        private static void InitWindows()
        {
            // Windows 100%
            AddWindows("Windows 1366x768 100%", 1366, 768, 1366, 768, 1.0f);
            AddWindows("Windows 1440x900 100%", 1440, 900, 1440, 900, 1.0f);
            AddWindows("Windows 1536x864 100%", 1536, 864, 1536, 864, 1.0f);
            AddWindows("Windows 1600x900 100%", 1600, 900, 1600, 900, 1.0f);
            AddWindows("Windows 1920x1080 100%", 1920, 1080, 1920, 1080, 1.0f);
            AddWindows("Windows 2560x1440 100%", 2560, 1440, 2560, 1440, 1.0f);
            AddWindows("Windows 3840x2160 100%", 3840, 2160, 3840, 2160, 1.0f);

            // Windows 125%
            AddWindows("Windows 1920x1080 125%", 1920, 1080, 1536, 864, 1.25f);
            AddWindows("Windows 2560x1440 125%", 2560, 1440, 2048, 1152, 1.25f);
            AddWindows("Windows 3840x2160 125%", 3840, 2160, 3072, 1728, 1.25f);

            // Windows 150%
            AddWindows("Windows 1920x1080 150%", 1920, 1080, 1280, 720, 1.5f);
            AddWindows("Windows 2560x1440 150%", 2560, 1440, 1707, 960, 1.5f);
            AddWindows("Windows 3840x2160 150%", 3840, 2160, 2560, 1440, 1.5f);

            // Windows 200%
            AddWindows("Windows 3840x2160 200%", 3840, 2160, 1920, 1080, 2.0f);
        }
    }
}