namespace Specrig.Tests;

public class RunsTests
{
    const string Paying = """
        Feature: Paying

          Scenario: The customer pays
            When the customer pays

          Scenario Outline: A refused payment is not taken
            When the customer pays in <currency>

            Examples:
              | currency |
              | EUR      |
              | USD      |
        """;

    readonly string dir = Directory.CreateTempSubdirectory().FullName;

    string FlowsFile => Path.Combine(dir, "flows.yaml");

    Runs Of(string feature)
    {
        var features = Path.Combine(dir, "feature");
        Directory.CreateDirectory(features);
        File.WriteAllText(Path.Combine(features, "pay.feature"), feature);
        return new Runs(new Features(features), FlowsFile);
    }

    static IReadOnlyList<Record> Paid() => [new Record(1, "in", "http", "POST /payments", "Action")];

    static (string, string)[] In(string currency) => [("currency", currency)];

    [Fact]
    public void A_whole_run_writes_the_record_with_the_rows_of_one_title_as_one_case()
    {
        var runs = Of(Paying);

        Assert.Empty(runs.Add("shop", "Paying", "The customer pays", [], Paid()));
        Assert.Empty(runs.Add("shop", "Paying", "A refused payment is not taken", In("EUR"), Paid()));
        Assert.Empty(runs.Add("shop", "Paying", "A refused payment is not taken", In("USD"), Paid()));

        Assert.Equal(["pay.feature: A refused payment is not taken", "pay.feature: The customer pays"],
            Assert.Single(Flows.Read(FlowsFile)!.Inputs[0].Branches).Cases);
    }

    [Fact]
    public void A_whole_run_of_an_outline_whose_title_names_a_column_writes_each_row_under_the_title_it_fills()
    {
        var runs = Of("""
            Feature: Paying

              Scenario Outline: Paying in <currency>
                When the customer pays in <currency>

                Examples:
                  | currency |
                  | EUR      |
                  | USD      |
            """);

        Assert.Empty(runs.Add("shop", "Paying", "Paying in <currency>", In("EUR"), Paid()));
        Assert.Empty(runs.Add("shop", "Paying", "Paying in <currency>", In("USD"), Paid()));

        Assert.Equal(["pay.feature: Paying in EUR", "pay.feature: Paying in USD"],
            Assert.Single(Flows.Read(FlowsFile)!.Inputs[0].Branches).Cases);
    }

    [Fact]
    public void A_run_without_one_row_of_an_outline_writes_nothing_and_says_nothing()
    {
        var runs = Of(Paying);

        Assert.Empty(runs.Add("shop", "Paying", "The customer pays", [], Paid()));
        Assert.Empty(runs.Add("shop", "Paying", "A refused payment is not taken", In("EUR"), Paid()));

        Assert.False(File.Exists(FlowsFile));
    }

    [Fact]
    public void A_row_that_ran_and_is_no_case_fails_by_name_and_stands_for_no_other_row()
    {
        var runs = Of(Paying);
        runs.Add("shop", "Paying", "The customer pays", [], Paid());
        runs.Add("shop", "Paying", "A refused payment is not taken", In("EUR"), Paid());

        var failure = Assert.Single(runs.Add("shop", "Paying", "A refused payment is not taken", In("GBP"), Paid()));

        Assert.Contains("Paying: A refused payment is not taken (currency, GBP)", failure);
        Assert.False(File.Exists(FlowsFile));
    }

    [Fact]
    public void A_scenario_that_ran_and_is_no_case_fails_by_name_without_a_row()
    {
        var failure = Assert.Single(Of(Paying).Add("shop", "Refunds", "The customer is refunded", [], Paid()));

        Assert.EndsWith("Refunds: The customer is refunded", failure);
    }

    [Fact]
    public void A_whole_run_needs_no_ignored_scenario_or_row()
    {
        var runs = Of("""
            Feature: Paying

              @ignore
              Scenario: The customer pays later
                When the customer pays

              Scenario Outline: A refused payment is not taken
                When the customer pays in <currency>

                Examples:
                  | currency |
                  | EUR      |

                @ignore
                Examples:
                  | currency |
                  | USD      |
            """);

        runs.Add("shop", "Paying", "A refused payment is not taken", In("EUR"), Paid());

        Assert.True(File.Exists(FlowsFile));
    }

    [Fact]
    public void A_row_repeated_in_the_examples_runs_as_often_before_the_record_is_written()
    {
        var runs = Of("""
            Feature: Paying

              Scenario Outline: A refused payment is not taken
                When the customer pays in <currency>

                Examples:
                  | currency |
                  | EUR      |
                  | EUR      |
            """);

        runs.Add("shop", "Paying", "A refused payment is not taken", In("EUR"), Paid());
        Assert.False(File.Exists(FlowsFile));

        runs.Add("shop", "Paying", "A refused payment is not taken", In("EUR"), Paid());
        Assert.True(File.Exists(FlowsFile));
    }

    [Fact]
    public void A_description_that_lost_its_anchor_fails_the_case_that_ended_the_run()
    {
        File.WriteAllText(FlowsFile, Flows.Encode(new Flows.Doc
        {
            Service = "shop",
            Inputs = [new() { Message = "http GET /gone", Description = "Витрина" }],
        }));
        var runs = Of(Paying);

        Assert.Empty(runs.Add("shop", "Paying", "The customer pays", [], Paid()));
        Assert.Empty(runs.Add("shop", "Paying", "A refused payment is not taken", In("EUR"), Paid()));
        Assert.Contains("lost their anchor", Assert.Single(runs.Add("shop", "Paying", "A refused payment is not taken", In("USD"), Paid())));

        Assert.Equal("http GET /gone", Assert.Single(Flows.Read(FlowsFile)!.Orphaned!).Anchor);
    }
}
