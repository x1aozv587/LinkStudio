using Serilog;

namespace LinkStudio.Core.Services
{
    /// <summary>
    /// LinkStudio 全局应用运行日志配置（基于 Serilog）。
    /// 仅记录应用自身的启动、连接、异常等诊断信息，与串口收发数据日志完全分离。
    /// </summary>
    public static class AppLogging
    {
        private static readonly object ConfigGate = new();

        public static void Configure(bool autoSaveToFile, string? directory)
        {
            lock (ConfigGate)
            {
                var config = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .WriteTo.Debug();

                if (autoSaveToFile && !string.IsNullOrWhiteSpace(directory))
                {
                    try
                    {
                        Directory.CreateDirectory(directory);
                        var logPath = Path.Combine(directory, "app-.log");
                        config = config.WriteTo.File(
                            path: logPath,
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: 30,
                            shared: true);
                    }
                    catch (Exception error)
                    {
                        // 目录无权限或非法时保留 Debug 输出，不阻塞应用启动。
                        Log.Warning(error, "初始化应用运行日志文件目录 {Directory} 失败", directory);
                    }
                }

                Log.Logger = config.CreateLogger();
            }
        }

        public static void CloseAndFlush()
        {
            lock (ConfigGate)
            {
                Log.CloseAndFlush();
            }
        }
    }
}
