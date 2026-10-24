using System.Net;
using System.Net.Http.Json;

namespace Specrig.Tests;

public class BackendTests
{
    readonly Trace trace = new();
    readonly Backend shop = new("https://shop.test");
    readonly HttpClient client;

    public BackendTests()
    {
        var contract = new Contract(Path.Combine(AppContext.BaseDirectory, "fixtures", "shop.yaml"), "shop", "Error");
        shop.Attach("shop", trace, contract);
        client = new HttpClient(new StandIns([shop]).Handler());
    }

    [Fact]
    public async Task The_reply_naming_the_most_of_the_query_wins()
    {
        shop.Reply("GET", "/items/1", 200, new { id = "any" });
        shop.ReplyTo("GET", "/items/1", new Query { ["view"] = "full" }, 200, new { id = "full" });

        Assert.Equal("full", (await client.GetFromJsonAsync<Item>("https://shop.test/items/1?view=full&lang=en"))!.Id);
        Assert.Equal("any", (await client.GetFromJsonAsync<Item>("https://shop.test/items/1?lang=en"))!.Id);
    }

    [Fact]
    public async Task Of_two_replies_naming_as_much_of_the_query_the_later_wins()
    {
        shop.ReplyTo("GET", "/items/1", new Query { ["view"] = "full" }, 200, new { id = "view" });
        shop.ReplyTo("GET", "/items/1", new Query { ["lang"] = "en" }, 200, new { id = "lang" });

        Assert.Equal("lang", (await client.GetFromJsonAsync<Item>("https://shop.test/items/1?view=full&lang=en"))!.Id);
    }

    [Fact]
    public async Task A_call_no_step_answered_gets_a_501_and_is_named()
    {
        var answer = await client.GetAsync("https://shop.test/items/1?view=full");

        Assert.Equal(HttpStatusCode.NotImplemented, answer.StatusCode);
        Assert.Equal(["GET /items/1?view=full"], shop.Unanswered);
    }

    [Fact]
    public async Task A_held_request_waits_until_a_step_releases_it()
    {
        shop.Reply("GET", "/items/1", 200, new { id = "1" });
        shop.Hold("GET", "/items/1");

        var pending = client.GetAsync("https://shop.test/items/1");
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);
        Assert.Equal(["GET /items/1"], shop.Unreleased);

        shop.Release("GET", "/items/1");
        Assert.Equal(HttpStatusCode.OK, (await pending).StatusCode);
        Assert.Empty(shop.Unreleased);
    }

    [Fact]
    public async Task What_the_contract_does_not_allow_is_kept()
    {
        shop.Reply("POST", "/items/new", 204);

        await client.PostAsJsonAsync("https://shop.test/items/new", new { count = 1 });

        Assert.Single(shop.Violations);
    }

    [Fact]
    public async Task A_request_is_traced_by_the_route_of_the_contract_that_takes_it()
    {
        shop.Reply("GET", "/items/7", 200, new { id = "7" });

        await client.GetAsync("https://shop.test/items/7");

        var sent = Assert.Single(trace.Records);
        Assert.Equal(("out", "http", "GET /items/{id}"), (sent.Dir, sent.Kind, sent.Name));
    }

    [Fact]
    public async Task A_request_to_an_origin_no_stand_in_takes_fails_and_is_kept()
    {
        var standIns = new StandIns([shop]);

        await Assert.ThrowsAsync<HttpRequestException>(() => new HttpClient(standIns.Handler()).GetAsync("https://maps.example.com/tiles"));

        Assert.Equal(["GET https://maps.example.com/tiles"], standIns.Outside);
    }

    sealed record Item(string Id);
}
