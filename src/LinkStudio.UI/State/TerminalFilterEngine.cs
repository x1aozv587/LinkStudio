using System.Text.RegularExpressions;

namespace LinkStudio.UI.State
{
    /// <summary>
    /// 单条搜索/过滤规则。
    /// </summary>
    public sealed class TerminalFilterRule
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Pattern { get; set; } = string.Empty;
        public bool IsRegex { get; set; }
        public bool MatchCase { get; set; }
        public bool IsEnabled { get; set; } = true;
        public string ColorKey { get; set; } = "amber";
    }

    /// <summary>
    /// 高亮分段结果：ColorKey 为 null 表示普通文本，否则用对应主题色高亮渲染。
    /// </summary>
    public readonly record struct HighlightSegment(string Text, string? ColorKey);

    /// <summary>
    /// 编译后的过滤规则快照，避免每条日志重复构造 Regex。
    /// </summary>
    public sealed record CompiledFilterRule(
        TerminalFilterRule Rule,
        Regex? CompiledRegex);

    /// <summary>
    /// 多规则普通文本 / 正则匹配与高亮切分引擎。
    /// </summary>
    public static class TerminalFilterEngine
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

        public static bool TryValidateRule(string pattern, bool isRegex, bool matchCase, out string error)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                error = "请输入关键词或正则表达式。";
                return false;
            }

            if (isRegex)
            {
                try
                {
                    var options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                    _ = new Regex(pattern, options, RegexTimeout);
                }
                catch (ArgumentException ex)
                {
                    error = $"正则表达式无效：{ex.Message}";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        public static List<CompiledFilterRule> CompileEnabledRules(IEnumerable<TerminalFilterRule> rules)
        {
            var result = new List<CompiledFilterRule>();
            foreach (var rule in rules)
            {
                if (!rule.IsEnabled || string.IsNullOrEmpty(rule.Pattern))
                {
                    continue;
                }

                if (rule.IsRegex)
                {
                    try
                    {
                        var options = RegexOptions.Compiled | (rule.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                        result.Add(new CompiledFilterRule(rule, new Regex(rule.Pattern, options, RegexTimeout)));
                    }
                    catch (ArgumentException)
                    {
                        // 忽略非法正则，避免拖垮界面渲染。
                    }
                }
                else
                {
                    result.Add(new CompiledFilterRule(rule, null));
                }
            }

            return result;
        }

        public static bool IsMatch(string text, IReadOnlyList<CompiledFilterRule> compiledRules)
        {
            if (compiledRules.Count == 0 || string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (var item in compiledRules)
            {
                if (item.CompiledRegex is not null)
                {
                    try
                    {
                        if (item.CompiledRegex.IsMatch(text))
                        {
                            return true;
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // 超时跳过该规则，防止灾难性回溯卡死界面。
                    }
                }
                else
                {
                    var comparison = item.Rule.MatchCase
                        ? StringComparison.Ordinal
                        : StringComparison.OrdinalIgnoreCase;
                    if (text.Contains(item.Rule.Pattern, comparison))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static List<HighlightSegment> BuildSegments(string text, IReadOnlyList<CompiledFilterRule> compiledRules)
        {
            if (string.IsNullOrEmpty(text) || compiledRules.Count == 0)
            {
                return [new HighlightSegment(text, null)];
            }

            var spans = new List<(int Start, int Length, string ColorKey)>();
            foreach (var item in compiledRules)
            {
                if (item.CompiledRegex is not null)
                {
                    try
                    {
                        foreach (Match match in item.CompiledRegex.Matches(text))
                        {
                            if (match.Success && match.Length > 0)
                            {
                                spans.Add((match.Index, match.Length, item.Rule.ColorKey));
                            }
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // 超时跳过该规则的高亮收集。
                    }
                }
                else
                {
                    var comparison = item.Rule.MatchCase
                        ? StringComparison.Ordinal
                        : StringComparison.OrdinalIgnoreCase;
                    var searchStart = 0;
                    var pattern = item.Rule.Pattern;
                    while (searchStart < text.Length)
                    {
                        var index = text.IndexOf(pattern, searchStart, comparison);
                        if (index < 0)
                        {
                            break;
                        }

                        spans.Add((index, pattern.Length, item.Rule.ColorKey));
                        searchStart = index + pattern.Length;
                    }
                }
            }

            if (spans.Count == 0)
            {
                return [new HighlightSegment(text, null)];
            }

            // 按起始位置排序，位置相同优先保留更长的匹配，避免重叠区间破坏切分。
            spans.Sort((a, b) =>
            {
                var cmp = a.Start.CompareTo(b.Start);
                return cmp != 0 ? cmp : b.Length.CompareTo(a.Length);
            });

            var segments = new List<HighlightSegment>(spans.Count * 2 + 1);
            var cursor = 0;

            foreach (var span in spans)
            {
                if (span.Start < cursor)
                {
                    continue;
                }

                if (span.Start > cursor)
                {
                    segments.Add(new HighlightSegment(text.Substring(cursor, span.Start - cursor), null));
                }

                segments.Add(new HighlightSegment(text.Substring(span.Start, span.Length), span.ColorKey));
                cursor = span.Start + span.Length;
            }

            if (cursor < text.Length)
            {
                segments.Add(new HighlightSegment(text.Substring(cursor), null));
            }

            return segments;
        }
    }
}
