using System.Text.RegularExpressions;

namespace Specrig.Tool;

// agents/1 of the specrig protocol: the parts of specrig.md every stack shares, the slots this one's.
static partial class Shared
{
    static readonly string[] parts = ["header", "contributing", "short", "rule11", "reference"];

    const string Heading = "## Rule #11 — specification before code";

    const string Token = "@specrig.md";

    [GeneratedRegex(@"\{\{([a-z-]+)\}\}")]
    private static partial Regex Slot();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})")]
    private static partial Regex Fence();

    [GeneratedRegex("`+")]
    private static partial Regex Ticks();

    public static string Verdict(string? text)
    {
        if (text is null)
        {
            return "absent";
        }
        var (stale, referenced) = Scan(text);
        return stale ? "stale" : referenced ? "ok" : "unreferenced";
    }

    public static bool References(string text) => Scan(text).Referenced;

    static (bool Stale, bool Referenced) Scan(string text)
    {
        string? fence = null;
        var stale = false;
        var referenced = false;
        foreach (var line in text.Split('\n'))
        {
            if (Fence().Match(line) is { Success: true } opening)
            {
                var run = opening.Groups[1].Value;
                if (fence is null)
                {
                    fence = run;
                }
                else if (run[0] == fence[0] && run.Length >= fence.Length)
                {
                    fence = null;
                }
                continue;
            }
            if (fence is not null)
            {
                continue;
            }
            stale |= line.TrimEnd() == Heading;
            referenced |= StandsAlone(line);
        }
        return (stale, referenced);
    }

    static bool StandsAlone(string line)
    {
        var code = CodeSpans(line);
        for (var at = line.IndexOf(Token, StringComparison.Ordinal); at >= 0; at = line.IndexOf(Token, at + 1, StringComparison.Ordinal))
        {
            var end = at + Token.Length;
            var before = at == 0 || char.IsWhiteSpace(line[at - 1]);
            var after = end == line.Length || char.IsWhiteSpace(line[end]);
            if (before && after && !code.Any(span => at >= span.From && at < span.To))
            {
                return true;
            }
        }
        return false;
    }

    static List<(int From, int To)> CodeSpans(string line)
    {
        var runs = Ticks().Matches(line);
        var spans = new List<(int, int)>();
        for (var i = 0; i < runs.Count; i++)
        {
            for (var j = i + 1; j < runs.Count; j++)
            {
                if (runs[j].Length == runs[i].Length)
                {
                    spans.Add((runs[i].Index, runs[j].Index + runs[j].Length));
                    i = j;
                    break;
                }
            }
        }
        return spans;
    }

    static string Part(string name)
    {
        using var stream = typeof(Shared).Assembly.GetManifestResourceStream($"protocol/agents/{name}.md")
            ?? throw new ArgumentException($"no part \"{name}\" (have: {string.Join(", ", parts)})");
        return new StreamReader(stream).ReadToEnd();
    }

    public static string Render(string part, IReadOnlyDictionary<string, string> slots)
    {
        var known = parts.SelectMany(p => Slot().Matches(Part(p)).Select(m => m.Groups[1].Value)).ToHashSet();
        if (slots.Keys.Where(k => !known.Contains(k)).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } unknown)
        {
            throw new ArgumentException($"no part has the slots {string.Join(", ", unknown)}");
        }
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var line in Part(part).Split('\n'))
        {
            if (Slot().Match(line) is { Success: true } alone && alone.Value == line && slots.GetValueOrDefault(alone.Groups[1].Value) is null or "")
            {
                if (!slots.ContainsKey(alone.Groups[1].Value))
                {
                    missing.Add(alone.Groups[1].Value);
                }
                continue;
            }
            lines.Add(Slot().Replace(line, m =>
            {
                if (slots.TryGetValue(m.Groups[1].Value, out var value))
                {
                    return value;
                }
                missing.Add(m.Groups[1].Value);
                return "";
            }));
        }
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"{part} leaves {string.Join(", ", missing)} unfilled");
        }
        var text = string.Join('\n', lines);
        while (text.Contains("\n\n\n"))
        {
            text = text.Replace("\n\n\n", "\n\n");
        }
        return text;
    }

    public static string ModeRows() => string.Join('\n', Rules.Modes.Select(m => $"| {m.Name} | {Rules.Allows(m)} |"));

    public static string AreaRows(IReadOnlyList<Rules.Area> areas) => string.Join('\n', areas
        .Select(a => (Area: a, Paths: Rules.Claimed(areas, a)))
        .Where(a => a.Paths.Count > 0)
        .Select(a => $"| {a.Area.Name} | {Ticked(a.Paths)} | {a.Area.Modes switch
        {
            null => "as the table says",
            [] => "no mode",
            var modes => string.Join(", ", modes),
        }} | {(a.Area.Grant.Count == 0 ? "—" : string.Join(", ", a.Area.Grant))} |"));

    public static string Ticked(IEnumerable<string> paths) => "`" + string.Join("`, `", paths) + "`";
}
