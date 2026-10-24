using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Specrig.Tool.Tests;

public class RulesTests
{
    sealed class Markers
    {
        public string Specrig { get; set; } = "";
        public List<Case> Cases { get; set; } = [];
    }

    sealed class Case
    {
        public string Name { get; set; } = "";
        [YamlMember(Alias = "marker")]
        public string Text { get; set; } = "";
        public string? Error { get; set; }
    }

    public static TheoryData<string> MarkerFixtures() =>
        [.. Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "markers"), "*.yaml").Select(Path.GetFileName).OfType<string>()];

    [Theory]
    [MemberData(nameof(MarkerFixtures))]
    public void Rules_take_and_refuse_the_markers_as_the_protocol_s_fixtures_do(string name)
    {
        var fixture = new DeserializerBuilder().WithNamingConvention(LowerCaseNamingConvention.Instance).Build()
            .Deserialize<Markers>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "markers", name)));
        Assert.Equal("markers-fixtures/1", fixture.Specrig);

        var wrong = new List<string>();
        foreach (var c in fixture.Cases)
        {
            string? failed = null;
            try
            {
                Printed(c.Text);
            }
            catch (ArgumentException e)
            {
                failed = e.Message;
            }
            if (c.Error is null ? failed is not null : failed?.Contains(c.Error) != true)
            {
                wrong.Add($"{c.Name}: {failed ?? "taken"}, want {c.Error ?? "taken"}");
            }
        }

        Assert.Empty(wrong);
    }

    readonly string root = Directory.CreateTempSubdirectory().FullName;

    JsonElement Printed(string marker)
    {
        File.WriteAllText(Path.Combine(root, "specrig.yaml"), marker);
        return JsonDocument.Parse(Rules.Print(root)).RootElement;
    }

    static string[] Strings(JsonElement list) => [.. list.EnumerateArray().Select(e => e.GetString()!)];

    [Fact]
    public void The_rules_are_the_modes_the_classes_with_the_layout_and_the_areas()
    {
        var rules = Printed("agents:\n  areas:\n    gen:\n      paths: [src/Gen/]\n      modes: []\n");

        Assert.Equal("rules/1", rules.GetProperty("specrig").GetString());
        Assert.Equal(Rules.Modes.Select(m => m.Name), rules.GetProperty("modes").EnumerateArray().Select(m => m.GetProperty("name").GetString()));
        Assert.Equal("`tests/feature/**/*.feature`", rules.GetProperty("modes").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "spec").GetProperty("allows").GetString());
        var classes = rules.GetProperty("classes").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => Strings(c.GetProperty("patterns")));
        Assert.Equal(["humans", "generated", "platform", "cases", "tests", "production"], classes.Keys);
        Assert.Equal(["specrig.md", "tests/surface.yaml", "tests/checks.yaml"], classes["generated"]);
        Assert.Equal(Rules.Platform, classes["platform"]);
        var areas = rules.GetProperty("areas").EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!);
        Assert.Equal(["csproj", "flows", "gen", "steps"], areas.Keys);
        Assert.Empty(Strings(areas["gen"].GetProperty("modes")));
        Assert.False(areas["steps"].TryGetProperty("modes", out _));
    }

    [Fact]
    public void An_area_naming_a_mode_there_is_not_is_refused_by_name() =>
        Assert.Equal(
            "agents.areas.events: unknown mode \"review\" (have: spec, redgreen, platform, auto)",
            Assert.Throws<ArgumentException>(() => Printed("agents:\n  areas:\n    events:\n      paths: [../shared/]\n      grant: [review]\n")).Message);

    [Fact]
    public void An_area_with_no_paths_is_refused() =>
        Assert.Contains("agents.areas.events names no paths", Assert.Throws<ArgumentException>(() => Printed("agents:\n  areas:\n    events:\n      modes: [spec]\n")).Message);

    [Fact]
    public void The_area_table_says_how_each_area_opens_and_leaves_out_what_another_decides() =>
        Assert.Equal(
            "| api | `./api` | platform | — |\n| csproj | `tests/Specs.csproj` | platform | redgreen |\n| steps | `tests/steps/` | as the table says | platform |",
            Shared.AreaRows([
                new() { Name = "api", Paths = ["./api"], Modes = ["platform"] },
                new() { Name = "contracts", Paths = ["api/"], Modes = ["spec"] },
                new() { Name = "csproj", Paths = ["tests/Specs.csproj"], Modes = ["platform"], Grant = ["redgreen"] },
                new() { Name = "steps", Paths = ["tests/steps/"], Grant = ["platform"] },
            ]));
}
