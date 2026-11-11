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
    public void A_file_is_read_by_the_keywords_of_its_language()
    {
        var features = Write("ru.feature", """
            # language: ru
            Функция: Оплата

              # Причина.
              Сценарий: Клиент платит
                Когда клиент платит

              Структура сценария: Клиент платит в <валюта>
                Когда клиент платит в <валюта>

                Примеры:
                  | валюта |
                  | EUR    |
                  | USD    |

                @ignore
                Примеры:
                  | валюта |
                  | GBP    |

              Scenario: Not a case of this language
                When it is read as English
            """);

        Assert.Equal([("Клиент платит", false), ("Клиент платит в EUR", false), ("Клиент платит в USD", false), ("Клиент платит в GBP", true)],
            features.Cases.Select(c => (c.Title, c.Ignored)));
        Assert.All(features.Cases, c => Assert.Equal("Оплата", c.Feature));
        Assert.Equal("ru.feature: Клиент платит в USD", features.Find("Оплата", "Клиент платит в <валюта>", [("валюта", "USD")])!.Label);
    }

    [Fact]
    public void The_language_is_named_below_comments_and_blank_lines_and_nowhere_else()
    {
        Write("ru.feature", "# the plan\n\n  # language: ru\nФункция: Оплата\n  Сценарий: Платит\n");
        var features = Write("late.feature", "Feature: Late\n  # language: ru\n  Сценарий: Not read\n  Scenario: Read\n");

        Assert.Equal(["late.feature: Read", "ru.feature: Платит"], features.Cases.Select(c => c.Label));
    }

    [Fact]
    public void A_language_gherkin_does_not_know_is_read_as_english()
    {
        var features = Write("a.feature", "# language: xx\nFeature: A\n  Scenario: Read\n");

        Assert.Equal(["Read"], features.Cases.Select(c => c.Title));
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
