namespace LinkStudio.Core.Workspaces
{
    /// <summary>
    /// 自定义画板中可自由组合的可视化组件类型。
    /// </summary>
    public enum WidgetKind
    {
        /// <summary>
        /// 多通道实时波形图（含十字准心与底部缩略图时间轴）。
        /// </summary>
        Waveform = 0,

        /// <summary>
        /// 单字段 KPI 数值卡（大字号数值 + 迷你折线 + 极值均值）。
        /// </summary>
        KpiCard = 1,

        /// <summary>
        /// GNSS 极坐标卫星天空图。
        /// </summary>
        Skyplot = 2,

        /// <summary>
        /// GNSS 多星座卫星载噪比柱状图。
        /// </summary>
        SatelliteSnr = 3,

        /// <summary>
        /// 实时结构化字段表（可按关键词筛选）。
        /// </summary>
        FieldTable = 4
    }

    /// <summary>
    /// 单个画板组件的持久化配置。
    /// 预留 DataSourceId 字段（默认 "*" 表示跟随当前活跃设备），以便后续平滑扩展 COM + TCP + BLE 多源并发绑定。
    /// </summary>
    public sealed class WidgetConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

        public WidgetKind Type { get; set; } = WidgetKind.Waveform;

        public string Title { get; set; } = string.Empty;

        // 基于 12 列网格布局的跨度：4（1/3 宽）、6（半宽）、8（2/3 宽）、12（整行）。
        public int ColSpan { get; set; } = 6;

        // 逻辑数据源标识；"*" 表示订阅当前活跃或全部来源，未来可绑定具体串口/TCP/BLE 逻辑源。
        public string DataSourceId { get; set; } = "*";

        // 绑定的数值字段名列表（如 imu.ax、temp、gga.altitude）。
        public List<string> BoundFields { get; set; } = [];

        // 字段表组件可选的筛选关键词。
        public string? FieldFilter { get; set; }

        public static WidgetConfig CreateDefault(WidgetKind type) => type switch
        {
            WidgetKind.Waveform => new WidgetConfig
            {
                Type = WidgetKind.Waveform,
                Title = "实时波形",
                ColSpan = 12
            },
            WidgetKind.KpiCard => new WidgetConfig
            {
                Type = WidgetKind.KpiCard,
                Title = "数据指标",
                ColSpan = 12
            },
            WidgetKind.Skyplot => new WidgetConfig
            {
                Type = WidgetKind.Skyplot,
                Title = "GNSS 天空图",
                ColSpan = 6
            },
            WidgetKind.SatelliteSnr => new WidgetConfig
            {
                Type = WidgetKind.SatelliteSnr,
                Title = "卫星载噪比",
                ColSpan = 6
            },
            WidgetKind.FieldTable => new WidgetConfig
            {
                Type = WidgetKind.FieldTable,
                Title = "实时字段表",
                ColSpan = 6
            },
            _ => new WidgetConfig { Type = type }
        };
    }

    /// <summary>
    /// 可保存与分享的自定义调试工作空间文档（对应磁盘上的 .linkworkspace JSON 文件）。
    /// 与具体 COM 端口号解耦，导入到其他机器后可直接绑定新串口运行。
    /// </summary>
    public sealed class WorkspaceDocument
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];

        public string Name { get; set; } = "自定义画板";

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public List<WidgetConfig> Widgets { get; set; } = [];

        public static WorkspaceDocument CreateBlank(string? name = null) => new()
        {
            Name = string.IsNullOrWhiteSpace(name) ? "空白画板" : name.Trim(),
            Widgets = []
        };

        public static WorkspaceDocument CreateImuTemplate(string? name = null) => new()
        {
            Name = string.IsNullOrWhiteSpace(name) ? "IMU 调试" : name.Trim(),
            Widgets =
            [
                new WidgetConfig
                {
                    Type = WidgetKind.KpiCard,
                    Title = "IMU 核心指标",
                    ColSpan = 12,
                    BoundFields = ["imu.ax", "imu.ay", "imu.az", "temp"]
                },
                new WidgetConfig
                {
                    Type = WidgetKind.Waveform,
                    Title = "三轴加速度波形",
                    ColSpan = 12,
                    BoundFields = ["imu.ax", "imu.ay", "imu.az"]
                }
            ]
        };

        public static WorkspaceDocument CreateGnssTemplate(string? name = null) => new()
        {
            Name = string.IsNullOrWhiteSpace(name) ? "GNSS 监控" : name.Trim(),
            Widgets =
            [
                new WidgetConfig
                {
                    Type = WidgetKind.Skyplot,
                    Title = "卫星天空图",
                    ColSpan = 6
                },
                new WidgetConfig
                {
                    Type = WidgetKind.SatelliteSnr,
                    Title = "卫星载噪比",
                    ColSpan = 6
                },
                new WidgetConfig
                {
                    Type = WidgetKind.Waveform,
                    Title = "海拔与 HDOP 趋势",
                    ColSpan = 12,
                    BoundFields = ["gga.altitude", "gga.hdop"]
                }
            ]
        };
    }
}
