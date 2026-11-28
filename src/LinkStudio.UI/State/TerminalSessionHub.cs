using System.Text;
using LinkStudio.Core.Devices;

namespace LinkStudio.UI.State
{
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
        private ICommunicationDevice? _activeDevice;
        private long _nextEntryId;

        public event Action? EntriesChanged;
        public event Action? RulesChanged;
        public event Action<long>? JumpToEntryRequested;
        public event Action? ConnectionStateChanged;

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
