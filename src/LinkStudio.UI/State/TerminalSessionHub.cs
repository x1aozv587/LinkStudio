using System.Text;
using LinkStudio.Core.Devices;
using LinkStudio.Core.Protocols;

namespace LinkStudio.UI.State
{
    public enum ProtocolFilterMode
    {
        Auto,
        NmeaOnly,
        JsonOnly
    }

    public sealed record LiveFieldState(
        string Key,
        string ProtocolName,
        string DisplayValue,
        string? Unit,
        ParsedFieldKind Kind,
        int UpdateCount,
        long LastUpdatedMs);

    public sealed record SatelliteSignal(
        string Talker,
        string Prn,
        double SnrDb,
        double? Elevation,
        double? Azimuth,
        long LastUpdatedMs);

    public readonly record struct NumericSample(long TimestampMs, double Value);

    public sealed record NumericSeriesSnapshot(
        string Key,
        string ProtocolName,
        string? Unit,
        double Latest,
        double Min,
        double Max,
        double Avg,
        int SampleCount,
        IReadOnlyList<NumericSample> Samples);

    public sealed class GnssSummarySnapshot
    {
        public bool HasData { get; init; }
        public int? FixQuality { get; init; }
        public int? FixType { get; init; }
        public string? RmcStatus { get; init; }
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
        public double? AltitudeM { get; init; }
        public double? SpeedKmh { get; init; }
        public int? SatellitesUsed { get; init; }
        public double? Hdop { get; init; }
        public double? Pdop { get; init; }
        public string? UtcTime { get; init; }
        public string? AntennaAlert { get; init; }
        public IReadOnlyList<string> ChipNotes { get; init; } = [];
        public IReadOnlyList<SatelliteSignal> Satellites { get; init; } = [];
    }

    /// <summary>
    /// 单条终端收发或系统日志条目（供主终端窗口与独立搜索/过滤窗口共享）。
    /// </summary>
    public sealed record TerminalLogEntry(
        long EntryId,
        DateTimeOffset Timestamp,
        string Direction,
        uint? SeqNo,
        byte[] Payload,
        string? SystemMessage = null);

    /// <summary>
    /// 终端日志与多规则过滤状态的共享中心（单例）。
    /// 主窗口写入日志、过滤窗口实时编辑规则，两边通过事件毫秒级联动。
    /// </summary>
    public sealed class TerminalSessionHub
    {
        public static readonly string[] FilterColors = ["amber", "emerald", "pink", "sky", "violet"];

        private readonly object _gate = new();
        private readonly List<TerminalLogEntry> _entries = [];
        private readonly List<TerminalFilterRule> _rules = [];
        private IReadOnlyList<CompiledFilterRule> _compiledRules = [];
        private readonly List<ParsedFrame> _parsedFrames = [];
        private readonly Dictionary<string, LiveFieldState> _liveFields = new(StringComparer.Ordinal);
        private readonly Dictionary<string, NumericSeriesBuffer> _numericSeries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SatelliteSignal> _satellites = new(StringComparer.Ordinal);
        private readonly List<string> _chipNotes = [];
        private string? _antennaAlert;
        private int _totalParsedCount;
        private int _invalidFrameCount;
        private ProtocolFilterMode _protocolFilter = ProtocolFilterMode.Auto;
        private ICommunicationDevice? _activeDevice;
        private long _nextEntryId;

        // 可视化页面的会话级记忆状态：切换主导航页（终端 / 协议 / 可视化）后保留用户已选字段与缩放区间。
        private readonly List<string> _dashboardPinnedKeys = [];
        private readonly HashSet<string> _dashboardHiddenKeys = new(StringComparer.Ordinal);
        private int _dashboardActiveTab;
        private double _waveViewStart = 0.0;
        private double _waveViewEnd = 1.0;

        public event Action? EntriesChanged;
        public event Action? RulesChanged;
        public event Action<long>? JumpToEntryRequested;
        public event Action? ConnectionStateChanged;
        public event Action? ProtocolDataChanged;

        public ProtocolFilterMode ProtocolFilter
        {
            get
            {
                lock (_gate)
                {
                    return _protocolFilter;
                }
            }
            set
            {
                lock (_gate)
                {
                    if (_protocolFilter == value)
                    {
                        return;
                    }
                    _protocolFilter = value;
                }

                ProtocolDataChanged?.Invoke();
            }
        }

        public int TotalParsedCount
        {
            get { lock (_gate) { return _totalParsedCount; } }
        }

        public int InvalidFrameCount
        {
            get { lock (_gate) { return _invalidFrameCount; } }
        }

        public void AddParsedFrame(ParsedFrame frame)
        {
            const int MaxRecentFrames = 150;
            lock (_gate)
            {
                _totalParsedCount++;
                if (!frame.IsValid)
                {
                    _invalidFrameCount++;
                }

                _parsedFrames.Add(frame);
                if (_parsedFrames.Count > MaxRecentFrames)
                {
                    _parsedFrames.RemoveRange(0, _parsedFrames.Count - MaxRecentFrames);
                }

                if (frame.IsValid)
                {
                    foreach (var field in frame.Fields)
                    {
                        var count = _liveFields.TryGetValue(field.Key, out var existing)
                            ? existing.UpdateCount + 1
                            : 1;
                        _liveFields[field.Key] = new LiveFieldState(
                            field.Key,
                            frame.ProtocolName,
                            field.DisplayValue,
                            field.Unit,
                            field.Kind,
                            count,
                            frame.TimestampMs);

                        if (field.Kind == ParsedFieldKind.Number
                            && field.NumberValue is double numericValue
                            && double.IsFinite(numericValue))
                        {
                            if (!_numericSeries.TryGetValue(field.Key, out var buffer))
                            {
                                buffer = new NumericSeriesBuffer(field.Key, frame.ProtocolName, field.Unit);
                                _numericSeries[field.Key] = buffer;
                            }

                            buffer.Add(frame.TimestampMs, numericValue, frame.ProtocolName, field.Unit);
                        }
                    }

                    if (frame.ProtocolName == "NMEA-0183")
                    {
                        UpdateGnssCachesUnsafe(frame);
                    }
                }
            }

            ProtocolDataChanged?.Invoke();
        }

        public void ClearProtocolData()
        {
            lock (_gate)
            {
                _parsedFrames.Clear();
                _liveFields.Clear();
                _numericSeries.Clear();
                _satellites.Clear();
                _chipNotes.Clear();
                _antennaAlert = null;
                _totalParsedCount = 0;
                _invalidFrameCount = 0;
            }

            ProtocolDataChanged?.Invoke();
        }

        public IReadOnlyList<LiveFieldState> GetLiveFieldsSnapshot()
        {
            lock (_gate)
            {
                return _liveFields.Values
                    .Where(f => MatchesProtocolFilter(f.ProtocolName, _protocolFilter))
                    .OrderBy(f => f.ProtocolName, StringComparer.Ordinal)
                    .ThenBy(f => f.Key, StringComparer.Ordinal)
                    .ToArray();
            }
        }

        public IReadOnlyList<ParsedFrame> GetParsedFramesSnapshot()
        {
            lock (_gate)
            {
                return _parsedFrames
                    .Where(f => MatchesProtocolFilter(f.ProtocolName, _protocolFilter))
                    .Reverse()
                    .ToArray();
            }
        }

        public IReadOnlyList<string> GetNumericFieldKeysSnapshot()
        {
            lock (_gate)
            {
                return _numericSeries.Keys
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToArray();
            }
        }

        public IReadOnlyList<NumericSeriesSnapshot> GetNumericSeriesSnapshot(IEnumerable<string>? keys = null)
        {
            lock (_gate)
            {
                if (keys is null)
                {
                    return _numericSeries.Values
                        .OrderBy(b => b.Key, StringComparer.Ordinal)
                        .Select(b => b.ToSnapshot())
                        .ToArray();
                }

                var result = new List<NumericSeriesSnapshot>();
                foreach (var key in keys)
                {
                    if (_numericSeries.TryGetValue(key, out var buffer))
                    {
                        result.Add(buffer.ToSnapshot());
                    }
                }

                return result;
            }
        }

        public GnssSummarySnapshot GetGnssSnapshot()
        {
            lock (_gate)
            {
                var hasNmea = _liveFields.Values.Any(f => f.ProtocolName == "NMEA-0183")
                    || _satellites.Count > 0
                    || _antennaAlert is not null;
                if (!hasNmea)
                {
                    return new GnssSummarySnapshot { HasData = false };
                }

                return new GnssSummarySnapshot
                {
                    HasData = true,
                    FixQuality = TryGetIntFieldUnsafe("gga.fixQuality"),
                    FixType = TryGetIntFieldUnsafe("gsa.fixType"),
                    RmcStatus = TryGetTextFieldUnsafe("rmc.status"),
                    Latitude = TryGetDoubleFieldUnsafe("gga.lat") ?? TryGetDoubleFieldUnsafe("rmc.lat") ?? TryGetDoubleFieldUnsafe("gll.lat"),
                    Longitude = TryGetDoubleFieldUnsafe("gga.lon") ?? TryGetDoubleFieldUnsafe("rmc.lon") ?? TryGetDoubleFieldUnsafe("gll.lon"),
                    AltitudeM = TryGetDoubleFieldUnsafe("gga.altitude"),
                    SpeedKmh = TryGetDoubleFieldUnsafe("rmc.speedKmh") ?? TryGetDoubleFieldUnsafe("vtg.speedKmh"),
                    SatellitesUsed = TryGetIntFieldUnsafe("gga.satellites"),
                    Hdop = TryGetDoubleFieldUnsafe("gga.hdop") ?? TryGetDoubleFieldUnsafe("gsa.hdop"),
                    Pdop = TryGetDoubleFieldUnsafe("gsa.pdop"),
                    UtcTime = TryGetTextFieldUnsafe("gga.utc") ?? TryGetTextFieldUnsafe("rmc.utc") ?? TryGetTextFieldUnsafe("zda.utc"),
                    AntennaAlert = _antennaAlert,
                    ChipNotes = _chipNotes.ToArray(),
                    Satellites = _satellites.Values
                        .OrderBy(s => s.Talker, StringComparer.Ordinal)
                        .ThenByDescending(s => s.SnrDb)
                        .ToArray()
                };
            }
        }

        private void UpdateGnssCachesUnsafe(ParsedFrame frame)
        {
            if (frame.FrameType == "TXT")
            {
                var msg = frame.Fields.FirstOrDefault(f => f.Key == "txt.message")?.TextValue;
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    if (msg.Contains("ANTENNA", StringComparison.OrdinalIgnoreCase))
                    {
                        _antennaAlert = msg;
                    }
                    else if ((msg.StartsWith("IC=", StringComparison.OrdinalIgnoreCase)
                        || msg.StartsWith("SW=", StringComparison.OrdinalIgnoreCase)
                        || msg.StartsWith("MA=", StringComparison.OrdinalIgnoreCase))
                        && !_chipNotes.Contains(msg, StringComparer.Ordinal))
                    {
                        _chipNotes.Add(msg);
                    }
                }
            }
            else if (frame.FrameType == "GSV")
            {
                var talker = frame.Fields.FirstOrDefault(f => f.Key == "nmea.talker")?.TextValue ?? "GN";
                foreach (var field in frame.Fields)
                {
                    // 匹配 gsv.{talker}.{prn}.snr 键名，更新该卫星最新信噪比。
                    if (!field.Key.EndsWith(".snr", StringComparison.Ordinal) || field.NumberValue is not double snr)
                    {
                        continue;
                    }

                    var segs = field.Key.Split('.');
                    if (segs.Length == 4)
                    {
                        var prn = segs[2];
                        var satKey = $"{talker}-{prn}";
                        var elev = frame.Fields.FirstOrDefault(f => f.Key == $"gsv.{talker}.{prn}.elev")?.NumberValue;
                        var azim = frame.Fields.FirstOrDefault(f => f.Key == $"gsv.{talker}.{prn}.azim")?.NumberValue;
                        _satellites[satKey] = new SatelliteSignal(talker, prn, snr, elev, azim, frame.TimestampMs);
                    }
                }
            }
        }

        private static bool MatchesProtocolFilter(string protocolName, ProtocolFilterMode filter) => filter switch
        {
            ProtocolFilterMode.NmeaOnly => protocolName == "NMEA-0183",
            ProtocolFilterMode.JsonOnly => protocolName == "JSON",
            _ => true
        };

        private double? TryGetDoubleFieldUnsafe(string key) =>
            _liveFields.TryGetValue(key, out var field) && double.TryParse(field.DisplayValue, out var v) ? v : null;

        private int? TryGetIntFieldUnsafe(string key) =>
            TryGetDoubleFieldUnsafe(key) is double d ? (int)Math.Round(d) : null;

        private string? TryGetTextFieldUnsafe(string key) =>
            _liveFields.TryGetValue(key, out var field) ? field.DisplayValue : null;

        public bool IsDeviceOpen => _activeDevice?.IsOpen == true;

        public string ActiveDeviceName => _activeDevice?.Name ?? string.Empty;

        public void SetActiveDevice(ICommunicationDevice? device)
        {
            _activeDevice = device;
            ConnectionStateChanged?.Invoke();
        }

        public Task SendBytesAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            var device = _activeDevice;
            if (device is null || !device.IsOpen)
            {
                throw new InvalidOperationException("请先在主窗口打开串口或虚拟设备。");
            }

            return device.SendAsync(payload, ct);
        }

        public long NextEntryId() => Interlocked.Increment(ref _nextEntryId);

        public void RequestJumpToEntry(long entryId) => JumpToEntryRequested?.Invoke(entryId);

        public int EntryCount
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count;
                }
            }
        }

        public IReadOnlyList<CompiledFilterRule> CompiledRules => _compiledRules;

        public IReadOnlyList<TerminalLogEntry> GetEntriesSnapshot()
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }

        public IReadOnlyList<TerminalFilterRule> GetRulesSnapshot()
        {
            lock (_gate)
            {
                return _rules.ToArray();
            }
        }

        public void AddEntry(TerminalLogEntry entry, int maxDisplayLines, bool preserveTop = false)
        {
            lock (_gate)
            {
                _entries.Add(entry);
                // 用户向上翻阅历史时暂停顶部裁剪（保留至多 5000 行安全上限），防止正在阅读的内容被顶走。
                var limit = preserveTop
                    ? Math.Max(maxDisplayLines, 5000)
                    : Math.Max(maxDisplayLines, 50);
                if (_entries.Count > limit)
                {
                    _entries.RemoveRange(0, _entries.Count - limit);
                }
            }

            EntriesChanged?.Invoke();
        }

        public void AddEntries(IReadOnlyList<TerminalLogEntry> entries, int maxDisplayLines, bool preserveTop = false)
        {
            if (entries.Count == 0)
            {
                return;
            }

            lock (_gate)
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    _entries.Add(entries[i]);
                }

                var limit = preserveTop
                    ? Math.Max(maxDisplayLines, 5000)
                    : Math.Max(maxDisplayLines, 50);
                if (_entries.Count > limit)
                {
                    _entries.RemoveRange(0, _entries.Count - limit);
                }
            }

            EntriesChanged?.Invoke();
        }

        public void TrimEntries(int maxDisplayLines)
        {
            var changed = false;
            lock (_gate)
            {
                var limit = Math.Max(maxDisplayLines, 50);
                if (_entries.Count > limit)
                {
                    _entries.RemoveRange(0, _entries.Count - limit);
                    changed = true;
                }
            }

            if (changed)
            {
                EntriesChanged?.Invoke();
            }
        }

        public void ClearEntries()
        {
            lock (_gate)
            {
                _entries.Clear();
            }

            EntriesChanged?.Invoke();
        }

        public TerminalFilterRule AddRule(string pattern = "", bool isRegex = false, bool matchCase = false, string? colorKey = null)
        {
            TerminalFilterRule rule;
            lock (_gate)
            {
                var chosenColor = colorKey ?? FilterColors[_rules.Count % FilterColors.Length];
                rule = new TerminalFilterRule
                {
                    Pattern = pattern,
                    IsRegex = isRegex,
                    MatchCase = matchCase,
                    ColorKey = chosenColor,
                    IsEnabled = true
                };
                _rules.Add(rule);
                RebuildCompiledRulesUnsafe();
            }

            RulesChanged?.Invoke();
            return rule;
        }

        public void UpdateRule(Guid id, Action<TerminalFilterRule> mutate)
        {
            lock (_gate)
            {
                var target = _rules.FirstOrDefault(r => r.Id == id);
                if (target is null)
                {
                    return;
                }

                mutate(target);
                RebuildCompiledRulesUnsafe();
            }

            RulesChanged?.Invoke();
        }

        public void RemoveRule(Guid id)
        {
            lock (_gate)
            {
                _rules.RemoveAll(r => r.Id == id);
                RebuildCompiledRulesUnsafe();
            }

            RulesChanged?.Invoke();
        }

        public void ClearRules()
        {
            lock (_gate)
            {
                _rules.Clear();
                RebuildCompiledRulesUnsafe();
            }

            RulesChanged?.Invoke();
        }

        public static string FormatEntryText(TerminalLogEntry entry, TerminalDisplayMode displayMode)
        {
            if (entry.SystemMessage is not null)
            {
                return entry.SystemMessage;
            }

            if (entry.Payload.Length == 0)
            {
                return "(空)";
            }

            return displayMode == TerminalDisplayMode.Hex
                ? BitConverter.ToString(entry.Payload).Replace("-", " ", StringComparison.Ordinal)
                : Encoding.UTF8.GetString(entry.Payload);
        }

        public static string BuildCopyLine(TerminalLogEntry entry, SerialWorkspaceSettings serial)
        {
            var parts = new List<string>(3);
            if (serial.ShowTimestamp)
            {
                parts.Add(entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"));
            }

            if (serial.ShowDirection)
            {
                parts.Add(entry.Direction);
            }

            if (serial.ShowPayload)
            {
                parts.Add(FormatEntryText(entry, serial.DisplayMode));
            }

            return string.Join(" ", parts);
        }

        public int DashboardActiveTab
        {
            get { lock (_gate) { return _dashboardActiveTab; } }
            set { lock (_gate) { _dashboardActiveTab = value; } }
        }

        public (double Start, double End) GetWaveViewRange()
        {
            lock (_gate)
            {
                return (_waveViewStart, _waveViewEnd);
            }
        }

        public void SetWaveViewRange(double start, double end)
        {
            lock (_gate)
            {
                _waveViewStart = Math.Clamp(start, 0.0, 0.95);
                _waveViewEnd = Math.Clamp(end, _waveViewStart + 0.05, 1.0);
            }
        }

        public IReadOnlyList<string> GetDashboardPinnedKeys()
        {
            lock (_gate)
            {
                return _dashboardPinnedKeys.ToArray();
            }
        }

        public IReadOnlySet<string> GetDashboardHiddenKeys()
        {
            lock (_gate)
            {
                return new HashSet<string>(_dashboardHiddenKeys, StringComparer.Ordinal);
            }
        }

        public void AddDashboardPinnedKey(string key)
        {
            var trimmed = key.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return;
            }

            lock (_gate)
            {
                if (!_dashboardPinnedKeys.Contains(trimmed, StringComparer.Ordinal))
                {
                    _dashboardPinnedKeys.Add(trimmed);
                }

                _dashboardHiddenKeys.Remove(trimmed);
            }
        }

        public void RemoveDashboardPinnedKey(string key)
        {
            lock (_gate)
            {
                _dashboardPinnedKeys.Remove(key);
                _dashboardHiddenKeys.Remove(key);
            }
        }

        public void ToggleDashboardKeyVisibility(string key)
        {
            lock (_gate)
            {
                if (!_dashboardHiddenKeys.Add(key))
                {
                    _dashboardHiddenKeys.Remove(key);
                }
            }
        }

        private void RebuildCompiledRulesUnsafe()
        {
            _compiledRules = TerminalFilterEngine.CompileEnabledRules(_rules);
        }

        private sealed class NumericSeriesBuffer
        {
            // 保留最近 2000 个采样点供波形图缩略图预览与历史回看，避免长时间运行内存无限增长。
            private const int MaxSamples = 2000;
            private readonly Queue<NumericSample> _samples = new(256);

            public NumericSeriesBuffer(string key, string protocolName, string? unit)
            {
                Key = key;
                ProtocolName = protocolName;
                Unit = unit;
            }

            public string Key { get; }
            public string ProtocolName { get; private set; }
            public string? Unit { get; private set; }

            public void Add(long timestampMs, double value, string protocolName, string? unit)
            {
                ProtocolName = protocolName;
                if (!string.IsNullOrEmpty(unit))
                {
                    Unit = unit;
                }

                if (_samples.Count >= MaxSamples)
                {
                    _samples.Dequeue();
                }

                _samples.Enqueue(new NumericSample(timestampMs, value));
            }

            public NumericSeriesSnapshot ToSnapshot()
            {
                var arr = _samples.ToArray();
                if (arr.Length == 0)
                {
                    return new NumericSeriesSnapshot(Key, ProtocolName, Unit, 0, 0, 0, 0, 0, arr);
                }

                var min = double.PositiveInfinity;
                var max = double.NegativeInfinity;
                var sum = 0.0;
                for (var i = 0; i < arr.Length; i++)
                {
                    var v = arr[i].Value;
                    if (v < min) min = v;
                    if (v > max) max = v;
                    sum += v;
                }

                return new NumericSeriesSnapshot(
                    Key,
                    ProtocolName,
                    Unit,
                    arr[^1].Value,
                    min,
                    max,
                    sum / arr.Length,
                    arr.Length,
                    arr);
            }
        }
    }
}
