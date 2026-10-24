using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Specrig.Tool.Tests;

public class SharedTests
{
    static Dictionary<string, string> Filled() => new()
    {
        ["what"] = "service", ["writer"] = "`dotnet specrig agents`", ["version"] = "`Specrig.Tool` 0.1.0",
        ["cut"] = "`tests/internal/Harness.cs`", ["validate"] = "`dotnet test tests`",
        ["boundary"] = "b", ["spec-paths"] = "s", ["platform-paths"] = "p", ["tasks"] = "", ["modes"] = Shared.ModeRows(),
        ["platform-files"] = "f", ["generated"] = "", ["tool"] = "specrig", ["config"] = "`specrig.yaml`", ["areas"] = "a",
        ["cases"] = "c", ["contracts"] = "", ["describes"] = "", ["binding"] = "x",
    };

    [Theory]
    [InlineData("header")]
    [InlineData("contributing")]
    [InlineData("short")]
    [InlineData("rule11")]
    [InlineData("reference")]
    public void Every_part_renders_with_every_slot_filled(string part)
    {
        var text = Shared.Render(part, Filled());

        Assert.DoesNotContain("{{", text);
        Assert.DoesNotContain("\n\n\n", text);
    }

    [Fact]
    public void A_slot_left_unfilled_is_named()
    {
        var slots = Filled();
        slots.Remove("binding");

        Assert.Contains("binding", Assert.Throws<InvalidOperationException>(() => Shared.Render("rule11", slots)).Message);
    }

    [Fact]
    public void A_slot_no_part_has_is_named()
    {
        var slots = Filled();
        slots["harness"] = "x";

        Assert.Contains("harness", Assert.Throws<ArgumentException>(() => Shared.Render("rule11", slots)).Message);
    }

    [Fact]
    public void An_empty_slot_alone_on_its_line_takes_the_line() =>
        Assert.Contains("None of the cycle below |\n| a **spike**", Shared.Render("rule11", Filled()));

    sealed class Fixture
    {
        public string Specrig { get; set; } = "";
        public List<Case> Cases { get; set; } = [];
    }

    sealed class Case
    {
        public string Name { get; set; } = "";
        public string? Agents { get; set; }
        public string Verdict { get; set; } = "";
    }

    public static TheoryData<string> Fixtures() =>
        [.. Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "agents"), "*.yaml").Select(Path.GetFileName).OfType<string>()];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void AGENTS_md_is_judged_as_the_protocol_s_fixtures_judge_it(string name)
    {
        var fixture = new DeserializerBuilder().WithNamingConvention(LowerCaseNamingConvention.Instance).Build()
            .Deserialize<Fixture>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "agents", name)));
        Assert.Equal("agents-fixtures/1", fixture.Specrig);

        var wrong = fixture.Cases.Where(c => Shared.Verdict(c.Agents) != c.Verdict).Select(c => $"{c.Name}: {Shared.Verdict(c.Agents)}, want {c.Verdict}").ToList();

        Assert.Empty(wrong);
    }
}
