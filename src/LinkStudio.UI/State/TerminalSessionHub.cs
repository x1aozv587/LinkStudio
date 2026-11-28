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
        private readonly Dictionary<string, SatelliteSignal> _satellites = new(StringComparer.Ordinal);
        private readonly List<string> _chipNotes = [];
        private string? _antennaAlert;
        private int _totalParsedCount;
        private int _invalidFrameCount;
        private ProtocolFilterMode _protocolFilter = ProtocolFilterMode.Auto;
        private ICommunicationDevice? _activeDevice;
        private long _nextEntryId;

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

        public void AddEntry(TerminalLogEntry entry, int maxDisplayLines)
        {
            lock (_gate)
            {
                _entries.Add(entry);
                var limit = Math.Max(maxDisplayLines, 50);
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

        private void RebuildCompiledRulesUnsafe()
        {
            _compiledRules = TerminalFilterEngine.CompileEnabledRules(_rules);
        }
    }
}
