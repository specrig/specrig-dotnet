using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Specrig.Tool;

// rules/1 over the layout init lays out; specrig.md renders the same table, so the two cannot disagree.
static class Rules
{
    public sealed class Mode
    {
        public string Name { get; set; } = "";
        public List<string> Opens { get; set; } = [];
        public List<string> Includes { get; set; } = [];
        public bool Offered { get; set; } = true;
        public string Allows { get; set; } = "";
        public string Brief { get; set; } = "";
    }

    public sealed class Class
    {
        public string Name { get; set; } = "";
        public List<string> Patterns { get; set; } = [];
    }

    public sealed class Area
    {
        public string Name { get; set; } = "";
        public List<string> Paths { get; set; } = [];
        public List<string>? Modes { get; set; }
        public List<string> Grant { get; set; } = [];
    }

    sealed class Table
    {
        public string Specrig { get; set; } = "";
        public List<Class> Classes { get; set; } = [];
        public List<Mode> Modes { get; set; } = [];
    }

    static readonly IDeserializer yaml = new DeserializerBuilder()
        .WithNamingConvention(LowerCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();

    // A marker is read untyped: a string where a list goes is the marker's mistake to
    // name, not a deserializer's exception.
    static readonly IDeserializer untyped = new DeserializerBuilder().Build();

    static readonly Table table = Load();

    public static IReadOnlyList<Mode> Modes => table.Modes;

    public const string Cases = "tests/feature/**/*.feature";

    public static readonly string[] Platform =
        ["tests/internal/", "tests/Specs.csproj", "tests/reqnroll.json", ".config/dotnet-tools.json", "specrig.yaml", "AGENTS.md", "CLAUDE.md"];

    public static readonly Dictionary<string, string[]> Layout = new()
    {
        ["generated"] = ["tests/surface.yaml", "tests/checks.yaml"],
        ["platform"] = Platform,
        ["cases"] = [Cases],
        ["tests"] = ["tests/"],
    };

    public static IReadOnlyList<Area> Defaults =>
    [
        new() { Name = "csproj", Paths = ["tests/Specs.csproj"], Modes = ["platform"], Grant = ["redgreen"] },
        new() { Name = "flows", Paths = ["tests/flows.yaml"], Modes = ["spec", "redgreen", "platform"] },
        new() { Name = "steps", Paths = ["tests/steps/"], Grant = ["platform"] },
    ];

    static Table Load()
    {
        using var stream = typeof(Rules).Assembly.GetManifestResourceStream("protocol/modes.yaml")!;
        var loaded = yaml.Deserialize<Table>(new StreamReader(stream).ReadToEnd());
        return loaded.Specrig == "modes/1" ? loaded : throw new InvalidOperationException($"modes.yaml is {loaded.Specrig}, and this tool reads modes/1");
    }

    public static Mode Lookup(string name) =>
        Modes.FirstOrDefault(m => m.Name == name) ?? throw new ArgumentException($"unknown mode \"{name}\" (have: {string.Join(", ", Modes.Select(m => m.Name))})");

    public static string Allows(Mode mode) => mode.Allows.Replace("{{cases}}", $"`{Cases}`");

    public static int Run(string[] args, TextWriter output)
    {
        output.WriteLine(args.Length == 0 ? Print(".") : throw new ArgumentException("rules takes no arguments"));
        return 0;
    }

    static object? Read(string marker)
    {
        try
        {
            return File.Exists(marker) ? untyped.Deserialize<object?>(File.ReadAllText(marker)) : null;
        }
        catch (YamlException e)
        {
            throw new ArgumentException($"specrig.yaml: {e.Message}");
        }
    }

    static object? Key(object? mapping, string key) =>
        mapping is IDictionary<object, object?> map && map.TryGetValue(key, out var value) ? value : null;

    static List<string> Strings(string name, string key, object? value, string what) =>
        value is List<object?> list && list.All(v => v is string)
            ? [.. list.Cast<string>()]
            : throw new ArgumentException($"agents.areas.{name}.{key} is not a list of {what}");

    static Area AreaOf(string name, object? value)
    {
        if (value is not IDictionary<object, object?> map)
        {
            throw new ArgumentException($"agents.areas.{name} is not a mapping of paths, modes and grant");
        }
        if (!map.TryGetValue("paths", out var paths) || paths is List<object?> { Count: 0 })
        {
            throw new ArgumentException($"agents.areas.{name} names no paths");
        }
        return new()
        {
            Name = name,
            Paths = Strings(name, "paths", paths, "paths"),
            Modes = map.TryGetValue("modes", out var modes) ? Strings(name, "modes", modes, "modes") : null,
            Grant = map.TryGetValue("grant", out var grant) ? Strings(name, "grant", grant, "modes") : [],
        };
    }

    // specrig.yaml replaces a default area by naming it.
    public static IReadOnlyList<Area> Areas(string marker)
    {
        var declared = Key(Key(Read(marker), "agents"), "areas");
        if (declared is not (null or IDictionary<object, object?>))
        {
            throw new ArgumentException("agents.areas is not a mapping of areas by name");
        }
        var areas = Defaults.ToDictionary(a => a.Name);
        foreach (var (key, value) in declared as IDictionary<object, object?> ?? new Dictionary<object, object?>())
        {
            var name = key.ToString() ?? "";
            areas[name] = AreaOf(name, value);
        }
        foreach (var area in areas.Values)
        {
            foreach (var mode in (area.Modes ?? []).Concat(area.Grant))
            {
                try
                {
                    Lookup(mode);
                }
                catch (ArgumentException e)
                {
                    throw new ArgumentException($"agents.areas.{area.Name}: {e.Message}");
                }
            }
        }
        return [.. areas.Values.OrderBy(a => a.Name, StringComparer.Ordinal)];
    }

    public static IReadOnlyList<string>? GuardOf(string marker) => Key(Read(marker), "guard") switch
    {
        List<object?> list when list.All(v => v is string) => [.. list.Cast<string>()],
        string line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries),
        _ => null,
    };

    static readonly JsonSerializerOptions json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Print(string root) => JsonSerializer.Serialize(new
    {
        Specrig = "rules/1",
        Modes = Modes.Select(m => new { m.Name, m.Opens, m.Includes, m.Offered, Allows = Allows(m), m.Brief }),
        Classes = table.Classes.Select(c => new { c.Name, Patterns = c.Patterns.Concat(Layout.GetValueOrDefault(c.Name) ?? []) }),
        Areas = Areas(Path.Combine(Path.GetFullPath(root), "specrig.yaml")),
    }, json);

    public static IReadOnlyList<string> Claimed(IReadOnlyList<Area> areas, Area area) =>
        [.. area.Paths.Where(p => !areas.Any(o => string.CompareOrdinal(o.Name, area.Name) < 0 && o.Paths.Any(q => Names(q) == Names(p))))];

    static string Names(string pattern)
    {
        var names = new List<string>();
        foreach (var name in pattern.Split('/'))
        {
            if (name is "" or ".")
            {
                continue;
            }
            if (name == ".." && names.Count > 0 && names[^1] != "..")
            {
                names.RemoveAt(names.Count - 1);
                continue;
            }
            names.Add(name);
        }
        return string.Join('/', names);
    }
}
