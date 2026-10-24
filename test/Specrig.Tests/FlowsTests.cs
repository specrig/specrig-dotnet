using System.Text.Json.Nodes;

namespace Specrig.Tests;

public class FlowsTests
{
    static Record In(long seq, string name, string? body = null, string? error = null, string step = "Action") =>
        new(seq, "in", "http", name, step) { Request = body is null ? null : JsonNode.Parse(body), Error = error };

    static Record Out(long seq, string name, string? body = null, string step = "Action") =>
        new(seq, "out", "http", name, step) { Request = body is null ? null : JsonNode.Parse(body) };

    static Flows.Doc Doc(List<Flows.Input> inputs, List<Flows.Orphan>? orphaned = null) =>
        new() { Service = "shop", Inputs = inputs, Orphaned = orphaned };

    static Flows.Input Input(string message, string? description = null) =>
        new() { Message = message, Description = description, Branches = [new() { Cases = ["shop.feature: The visitor sees the shop"] }] };

    [Fact]
    public void A_description_whose_input_is_gone_is_orphaned_and_comes_back_with_its_input()
    {
        var gone = Flows.Reconcile(Doc([Input("http GET /shop", "Витрина")]), Doc([]));
        var orphan = Assert.Single(gone.Orphaned!);
        Assert.Equal(("http GET /shop", "Витрина"), (orphan.Anchor, orphan.Description));

        var back = Flows.Reconcile(gone, Doc([Input("http GET /shop")]));
        Assert.Equal("Витрина", back.Inputs[0].Description);
        Assert.Null(back.Orphaned);
    }

    [Fact]
    public void The_record_says_first_that_it_is_specrigs() =>
        Assert.Equal(["specrig: flows/1", "service: shop"], Flows.Encode(Flows.Build("shop", [])).Split('\n')[2..4]);

    [Fact]
    public void Cases_that_sent_the_same_share_a_branch_and_a_refusal_is_a_branch_of_its_own()
    {
        var doc = Flows.Build("shop",
        [
            new("orders.feature: one", [In(1, "GET /orders/{id}"), Out(2, "GET /invoices/{orderId}")]),
            new("orders.feature: two", [In(1, "GET /orders/{id}"), Out(2, "GET /invoices/{orderId}")]),
            new("orders.feature: three", [In(1, "GET /orders/{id}", error: "HTTP 404 order_not_found")]),
        ]);

        var input = Assert.Single(doc.Inputs);
        Assert.Equal("http GET /orders/{id}", input.Message);
        Assert.Collection(input.Branches,
            refused =>
            {
                Assert.Equal("HTTP 404 order_not_found", refused.Refused);
                Assert.Equal(["orders.feature: three"], refused.Cases);
            },
            answered =>
            {
                Assert.Equal(["http GET /invoices/{orderId}"], answered.Sends);
                Assert.Equal(["orders.feature: one", "orders.feature: two"], answered.Cases);
            });
    }

    [Fact]
    public void A_block_sent_again_straight_after_itself_is_one_pass_of_a_loop()
    {
        var doc = Flows.Build("shop",
        [
            new("a.feature: loop", [In(1, "POST /orders"), Out(2, "GET /a"), Out(3, "GET /b"), Out(4, "GET /a"), Out(5, "GET /b"), Out(6, "GET /c")]),
        ]);

        Assert.Equal(["http GET /a", "http GET /b", "http GET /c"], doc.Inputs[0].Branches[0].Sends);
    }

    [Fact]
    public void A_need_is_a_field_that_splits_the_answered_branches_exactly()
    {
        var doc = Flows.Build("shop",
        [
            new("pay.feature: saved card", [In(1, "POST /orders", """{"card": {"id": "x"}, "items": [{"sku": "a"}]}"""), Out(2, "POST /payments")]),
            new("pay.feature: invoice", [In(1, "POST /orders", """{"items": [{"sku": "a"}]}""")]),
        ]);

        var needs = doc.Inputs[0].Needs!;
        Assert.Equal(["card.id"], needs["http POST /payments"]);
        Assert.Equal(["card.id", "items[].sku"], doc.Inputs[0].Fields);
    }

    [Fact]
    public void Only_what_a_when_reaches_is_an_input()
    {
        var doc = Flows.Build("shop",
        [
            new("a.feature: one", [In(1, "POST /setup", step: "Context"), Out(2, "GET /x", step: "Context"), In(3, "GET /orders/{id}"), Out(4, "GET /y")]),
        ]);

        var input = Assert.Single(doc.Inputs);
        Assert.Equal("http GET /orders/{id}", input.Message);
        Assert.Equal(["http GET /y"], input.Branches[0].Sends);
        Assert.Equal(["http GET /x", "http GET /y"], doc.Outputs!.Select(o => o.Message));
    }

    [Fact]
    public void A_record_reads_back_as_it_was_written()
    {
        var doc = Flows.Build("shop",
        [
            new("orders.feature: one", [In(1, "GET /orders/{id}", """{"page": ["2"]}"""), Out(2, "POST /reminders", """{"order": 7}""")]),
        ]);
        doc.Inputs[0].Description = "Заказ: что осталось оплатить\n\nВторой абзац";
        doc.Inputs[0].Branches[0].Description = "true";
        var file = Path.GetTempFileName();

        File.WriteAllText(file, Flows.Encode(doc));
        var read = Flows.Read(file)!;

        Assert.Equal(Flows.Encode(doc), Flows.Encode(read));
        Assert.Equal("Заказ: что осталось оплатить\n\nВторой абзац", read.Inputs[0].Description);
        Assert.Equal("true", read.Inputs[0].Branches[0].Description);
        Assert.Equal(["order"], read.Inputs[0].Branches[0].Sent!["http POST /reminders"]);
    }
}
