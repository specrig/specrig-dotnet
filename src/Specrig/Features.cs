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

    static IEnumerable<Case> Parse(string file, string[] lines)
    {
        var feature = "";
        var featureIgnored = false;
        var tags = new List<string>();
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
                yield return new Case(file, feature, title, featureIgnored || tags.Contains("@ignore"));
                tags.Clear();
                continue;
            }
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                tags.Clear();
            }
        }
    }

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
