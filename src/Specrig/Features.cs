using System.Text.RegularExpressions;

namespace Specrig;

public sealed record Case(string File, string Feature, string Title, bool Ignored)
{
    public string Label => $"{File}: {Title}";
}

public sealed class Features
{
    readonly List<Case> cases = [];

    public Features(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFiles(dir, "*.feature", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var file = Path.GetRelativePath(dir, path).Replace('\\', '/');
            cases.AddRange(Parse(file, File.ReadAllLines(path)));
        }
    }

    public IReadOnlyList<Case> Cases => cases;

    public Case? Find(string feature, string title) =>
        cases.Where(c => c.Feature == feature && c.Title == title).ToList() is [var one] ? one
        : cases.Where(c => c.Title == title).ToList() is [var only] ? only
        : null;

    public static string Filled(string title, IEnumerable<(string Column, string Value)> row) =>
        row.Aggregate(title, (filled, cell) => filled.Replace($"<{cell.Column}>", cell.Value));

    static List<Case> Parse(string file, string[] lines)
    {
        var found = new List<Case>();
        var feature = "";
        var featureIgnored = false;
        var tags = new List<string>();
        Case? outline = null;
        bool? rowsIgnored = null;
        string[]? header = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('@'))
            {
                tags.AddRange(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                continue;
            }
            if (line.StartsWith("Feature:"))
            {
                feature = line["Feature:".Length..].Trim();
                featureIgnored = tags.Contains("@ignore");
                tags.Clear();
                continue;
            }
            if (Scenario(line) is { } title)
            {
                outline = new Case(file, feature, title, featureIgnored || tags.Contains("@ignore"));
                found.Add(outline);
                tags.Clear();
                rowsIgnored = null;
                continue;
            }
            if (outline is not null && (line.StartsWith("Examples:") || line.StartsWith("Scenarios:")))
            {
                // An outline runs as its rows, never under its own title.
                found.RemoveAll(c => ReferenceEquals(c, outline));
                rowsIgnored = outline.Ignored || tags.Contains("@ignore");
                header = null;
                tags.Clear();
                continue;
            }
            if (rowsIgnored is { } ignored && line.StartsWith('|'))
            {
                var cells = Cells(line);
                if (header is null)
                {
                    header = cells;
                }
                else
                {
                    found.Add(outline! with { Title = Filled(outline.Title, header.Zip(cells)), Ignored = ignored });
                }
                continue;
            }
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                tags.Clear();
                // Past its header a table ends at the first other line, such as a Rule's Background.
                if (header is not null)
                {
                    rowsIgnored = null;
                }
            }
        }
        return found;
    }

    // gherkin's escapes in a table cell.
    static string[] Cells(string line) =>
        Regex.Matches(line[1..], @"(?:[^|\\]|\\.)*\|")
            .Select(m => Regex.Replace(m.Value[..^1], @"\\[|\\n]", e => e.Value == @"\n" ? "\n" : e.Value[1..]).Trim())
            .ToArray();

    static string? Scenario(string line)
    {
        foreach (var keyword in (string[])["Scenario Outline:", "Scenario Template:", "Scenario:", "Example:"])
        {
            if (line.StartsWith(keyword))
            {
                return line[keyword.Length..].Trim();
            }
        }
        return null;
    }
}
