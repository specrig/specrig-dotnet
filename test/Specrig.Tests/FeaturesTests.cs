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

        Assert.Equal(["orders/pay.feature: The customer pays", "orders/pay.feature: The customer pays in EUR"],
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
    public void An_outline_is_a_case_for_each_row_of_its_examples()
    {
        var features = Write("a.feature", """
            Feature: A

              Scenario Outline: Paying in <currency>
                When the customer pays in <currency>

                Examples:
                  | currency   |
                  | EUR        |
                  | EUR \| USD |

                @ignore
                Examples:
                  | currency |
                  | USD      |

              Rule: Refunds

                Background:
                  Given a paid order
                    | currency |
                    | GBP      |
            """);

        Assert.Equal([("Paying in EUR", false), ("Paying in EUR | USD", false), ("Paying in USD", true)],
            features.Cases.Select(c => (c.Title, c.Ignored)));
    }

    [Fact]
    public void A_scenario_is_found_by_its_feature_and_else_by_its_title_alone()
    {
        Write("a.feature", "Feature: A\n  Scenario: Shared\n  Scenario: Only here\n");
        var features = Write("b.feature", "Feature: B\n  Scenario: Shared\n");

        Assert.Equal("b.feature", features.Find("B", "Shared", [])!.File);
        Assert.Equal("a.feature", features.Find("Renamed", "Only here", [])!.File);
        Assert.Null(features.Find("Renamed", "Shared", []));
    }

    [Fact]
    public void Each_row_of_an_outline_whose_title_names_no_column_is_found_under_that_title()
    {
        var features = Write("a.feature", """
            Feature: A

              Scenario Outline: A refused payment is not taken
                When the customer pays in <currency>

                Examples:
                  | currency |
                  | EUR      |
                  | USD      |
            """);

        var eur = features.Find("A", "A refused payment is not taken", [("currency", "EUR")]);
        var usd = features.Find("A", "A refused payment is not taken", [("currency", "USD")]);

        Assert.NotEqual(eur, usd);
        Assert.Equal("a.feature: A refused payment is not taken", eur!.Label);
        Assert.Equal(eur.Label, usd!.Label);
    }

    [Fact]
    public void A_row_of_an_outline_whose_title_names_a_column_is_found_under_the_title_it_fills()
    {
        var features = Write("a.feature", """
            Feature: A

              Scenario Outline: Paying in <currency>
                When the customer pays in <currency>

                Examples:
                  | currency   |
                  | EUR \| USD |
            """);

        Assert.Equal("a.feature: Paying in EUR | USD", features.Find("A", "Paying in <currency>", [("currency", "EUR | USD")])!.Label);
    }

    [Fact]
    public void A_row_its_examples_do_not_have_is_not_found()
    {
        var features = Write("a.feature", """
            Feature: A

              Scenario Outline: A refused payment is not taken
                When the customer pays in <currency>

                Examples:
                  | currency |
                  | EUR      |
            """);

        Assert.Null(features.Find("A", "A refused payment is not taken", [("currency", "USD")]));
    }

    [Fact]
    public void A_row_in_examples_both_ignored_and_not_is_found_as_the_one_that_runs()
    {
        var features = Write("a.feature", """
            Feature: A

              Scenario Outline: A refused payment is not taken
                When the customer pays in <currency>

                @ignore
                Examples:
                  | currency |
                  | EUR      |

                Examples:
                  | currency |
                  | EUR      |
            """);

        Assert.False(features.Find("A", "A refused payment is not taken", [("currency", "EUR")])!.Ignored);
    }
}
