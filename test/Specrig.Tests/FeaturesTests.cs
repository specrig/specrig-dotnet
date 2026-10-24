namespace Specrig.Tests;

public class FeaturesTests
{
    readonly string dir = Directory.CreateTempSubdirectory().FullName;

    Features Write(string file, string body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, file))!);
        File.WriteAllText(Path.Combine(dir, file), body);
        return new Features(dir);
    }

    [Fact]
    public void A_case_is_named_by_its_file_below_the_directory_and_its_title()
    {
        var features = Write("orders/pay.feature", """
            Feature: Paying

              # The reason, in the present tense.
              Scenario: The customer pays
                When the customer pays

              Scenario Outline: The customer pays in <currency>
                When the customer pays in <currency>
                Examples:
                  | currency |
                  | EUR      |
            """);

        Assert.Equal(["orders/pay.feature: The customer pays", "orders/pay.feature: The customer pays in <currency>"],
            features.Cases.Select(c => c.Label));
    }

    [Fact]
    public void A_case_tagged_ignore_or_in_an_ignored_feature_is_known_as_ignored()
    {
        var features = Write("a.feature", """
            Feature: A

              @ignore @slow
              Scenario: Skipped
                When it runs

              Scenario: Run
                When it runs
            """);

        Assert.Equal([("Skipped", true), ("Run", false)], features.Cases.Select(c => (c.Title, c.Ignored)));
    }

    [Fact]
    public void A_scenario_is_found_by_its_feature_and_else_by_its_title_alone()
    {
        Write("a.feature", "Feature: A\n  Scenario: Shared\n  Scenario: Only here\n");
        var features = Write("b.feature", "Feature: B\n  Scenario: Shared\n");

        Assert.Equal("b.feature", features.Find("B", "Shared")!.File);
        Assert.Equal("a.feature", features.Find("Renamed", "Only here")!.File);
        Assert.Null(features.Find("Renamed", "Shared"));
    }
}
