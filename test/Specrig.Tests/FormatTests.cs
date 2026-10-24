using System.Text.Json;
using System.Text.Json.Nodes;

namespace Specrig.Tests;

public class FormatTests
{
    static readonly string fixtures = Path.Combine(AppContext.BaseDirectory, "records");

    public static TheoryData<string> Names() => [.. Directory.EnumerateFiles(fixtures, "*.trace.json").Select(f => Path.GetFileName(f)[..^".trace.json".Length])];

    [Theory]
    [MemberData(nameof(Names))]
    public void A_trace_comes_out_as_the_record_every_writer_agrees_on(string name)
    {
        var runs = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, $"{name}.trace.json")))!.AsArray().Select(run => new Run(
            run!["case"]!.GetValue<string>(),
            [.. run["records"]!.AsArray().Select(r => new Record(
                r!["seq"]!.GetValue<long>(), r["dir"]!.GetValue<string>(), r["kind"]!.GetValue<string>(), r["name"]!.GetValue<string>(), r["step"]?.GetValue<string>() ?? "")
            {
                Request = r["request"]?.DeepClone(),
                Error = r["error"]?.GetValue<string>(),
            })]));
        var actual = Flows.Encode(Flows.Build(name, runs));

        var expected = Path.Combine(fixtures, $"{name}.flows.yaml");
        Assert.True(File.Exists(expected), $"{name}.flows.yaml is missing; the record comes out as\n{actual}");
        Assert.Equal(File.ReadAllText(expected), actual);
    }
}
