namespace MainClient.Infrastructure
{
    public class AppSettings
    {

        /// <summary>
        /// 设备接口
        /// </summary>
        public string DevApiUrl { get; set; }

        /// <summary>
        /// 任务接口
        /// </summary>
        public string TaskApiUrl { get; set; }
        /// <summary>
        /// 任务名称
        /// </summary>
        public string TaskName { get; set; }

        /// <summary>
        /// 任务提取间隔
        /// </summary>
        public int TaskPullInterval { get; set; } = 1000;
        /// <summary>
        /// 单UV执行间隔
        /// </summary>
        public int UvExecutionInterval { get; set; } = 1000;
        /// <summary>
        /// 并发数量
        /// </summary>

        public int MaxConcurrency { get; set; }

        /// <summary>
        /// 倍率
        /// </summary>
        public int Multiple { get; set; }
        /// <summary>
        /// 主进程重置
        /// </summary>
        public int MainProcessResetIntervalMinutes { get; set; }
        /// <summary>
        /// 详细日志
        /// </summary>
        public bool IsDetailLog { get; set; }

        /// <summary>
        /// 代理模式
        /// </summary>
        public bool IsProxyMode { get; set; }
        /// <summary>
        ///  代理接口
        /// </summary>
        public string ProxyIpUrl { get; set; }

        /// <summary>
        /// IP检测
        /// </summary>
        public bool IsCheckIp { get; set; }
        /// <summary>
        /// 获取真实IP
        /// </summary>
        public bool IsRealIp { get; set; }
        /// <summary>
        /// IP有效期
        /// </summary>
        public int IpValidityDuration { get; set; }


        /// <summary>
        /// 隐藏模式
        /// </summary>
        public bool IsHiddenMode { get; set; }


        /// <summary>
        /// 测试模式
        /// </summary>
        public bool IsTest { get; set; }

        /// <summary>
        /// Osr模式
        /// </summary>
        public bool IsOsrMode { get; set; } = false;





    }
}
