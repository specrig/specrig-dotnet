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

    [Fact]
    public async Task A_reply_carries_the_headers_it_was_given()
    {
        shop.Reply("GET", "/items/1", 200, new { id = "1" }, new Dictionary<string, string> { ["ETag"] = "\"v2\"", ["X-Shard"] = "b" });

        var answer = await client.GetAsync("https://shop.test/items/1");

        Assert.Equal("\"v2\"", Assert.Single(answer.Headers.GetValues("ETag")));
        Assert.Equal("b", Assert.Single(answer.Headers.GetValues("X-Shard")));
        Assert.Empty(shop.Violations);
    }

    [Fact]
    public async Task A_reply_to_a_query_carries_its_own_headers()
    {
        shop.Reply("GET", "/items/1", 200, new { id = "any" }, new Dictionary<string, string> { ["X-Shard"] = "a" });
        shop.ReplyTo("GET", "/items/1", new Query { ["view"] = "full" }, 200, new { id = "full" }, new Dictionary<string, string> { ["X-Shard"] = "b" });

        Assert.Equal("b", (await client.GetAsync("https://shop.test/items/1?view=full")).Headers.GetValues("X-Shard").Single());
        Assert.Equal("a", (await client.GetAsync("https://shop.test/items/1")).Headers.GetValues("X-Shard").Single());
    }

    [Fact]
    public async Task A_content_header_replaces_the_one_the_body_brings()
    {
        shop.Reply("GET", "/items/1", 404, new { code = 7, message = "gone" }, new Dictionary<string, string> { ["Content-Type"] = "application/problem+json" });

        var answer = await client.GetAsync("https://shop.test/items/1");

        Assert.Equal("application/problem+json", answer.Content.Headers.ContentType?.MediaType);
        Assert.Equal(7, (await answer.Content.ReadFromJsonAsync<Failure>())!.Code);
    }

    [Fact]
    public async Task A_path_in_braces_takes_any_one_segment()
    {
        shop.Reply("GET", "/items/{id}", 200, new { id = "tpl" });

        Assert.Equal("tpl", (await client.GetFromJsonAsync<Item>("https://shop.test/items/7"))!.Id);
        Assert.Equal("tpl", (await client.GetFromJsonAsync<Item>("https://shop.test/items/8"))!.Id);
        Assert.Equal(HttpStatusCode.NotImplemented, (await client.GetAsync("https://shop.test/items/7/parts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented, (await client.GetAsync("https://shop.test/items")).StatusCode);
        Assert.Equal(["GET /items/7/parts", "GET /items"], shop.Unanswered);
    }

    [Fact]
    public async Task A_declaration_with_the_exact_path_wins_over_a_template()
    {
        shop.Reply("GET", "/items/{id}", 200, new { id = "tpl" });
        shop.Reply("GET", "/items/1", 200, new { id = "exact" });

        Assert.Equal("exact", (await client.GetFromJsonAsync<Item>("https://shop.test/items/1"))!.Id);
        Assert.Equal("tpl", (await client.GetFromJsonAsync<Item>("https://shop.test/items/2"))!.Id);
    }

    [Fact]
    public async Task Of_two_templates_the_first_declared_wins()
    {
        shop.Reply("GET", "/items/{id}", 200, new { id = "first" });
        shop.Reply("GET", "/items/{other}", 200, new { id = "second" });

        Assert.Equal("first", (await client.GetFromJsonAsync<Item>("https://shop.test/items/3"))!.Id);
    }

    [Fact]
    public async Task A_template_is_answered_by_the_query_as_a_path_is()
    {
        shop.Reply("GET", "/items/{id}", 200, new { id = "any" });
        shop.ReplyTo("GET", "/items/{id}", new Query { ["view"] = "full" }, 200, new { id = "full" });

        Assert.Equal("full", (await client.GetFromJsonAsync<Item>("https://shop.test/items/3?view=full"))!.Id);
        Assert.Equal("any", (await client.GetFromJsonAsync<Item>("https://shop.test/items/3"))!.Id);
    }

    [Fact]
    public async Task The_requests_a_template_takes_are_the_requests_to_it()
    {
        shop.Reply("GET", "/items/{id}", 200, new { id = "1" });

        await client.GetAsync("https://shop.test/items/1");
        await client.GetAsync("https://shop.test/items/2?view=full");

        Assert.Equal(["/items/1", "/items/2"], shop.Requests("GET", "/items/{id}").Select(r => r.Path));
        Assert.Equal(["/items/2"], shop.Requests("GET", "/items/2").Select(r => r.Path));
        Assert.Empty(shop.Requests("POST", "/items/{id}"));
    }

    [Fact]
    public async Task A_template_is_held_and_released_by_the_template()
    {
        shop.Reply("GET", "/items/{id}", 200, new { id = "1" });
        shop.Hold("GET", "/items/{id}");

        var pending = client.GetAsync("https://shop.test/items/1");
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);
        Assert.Equal(["GET /items/1"], shop.Unreleased);

        shop.Release("GET", "/items/{id}");
        Assert.Equal(HttpStatusCode.OK, (await pending).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("https://shop.test/items/2")).StatusCode);
        Assert.Empty(shop.Unreleased);
    }

    [Fact]
    public async Task A_hold_that_was_released_leaves_the_template_to_hold()
    {
        shop.Reply("GET", "/items/{id}", 200, new { id = "1" });
        shop.Hold("GET", "/items/1");
        shop.Release("GET", "/items/1");
        shop.Hold("GET", "/items/{id}");

        var pending = client.GetAsync("https://shop.test/items/1");
        await Task.Delay(100);

        Assert.False(pending.IsCompleted);
        shop.Release("GET", "/items/{id}");
        Assert.Equal(HttpStatusCode.OK, (await pending).StatusCode);
    }

    [Fact]
    public async Task The_contract_and_the_trace_name_the_request_by_its_real_path()
    {
        shop.Reply("GET", "/items/{id}", 204);

        await client.GetAsync("https://shop.test/items/7");

        Assert.Equal("/items/7", Assert.Single(shop.Requests("GET", "/items/{id}")).Path);
        Assert.Contains("/items/7", Assert.Single(shop.Violations));
        var traced = Assert.Single(trace.Records);
        Assert.Equal(("out", "http", "GET /items/{id}"), (traced.Dir, traced.Kind, traced.Name));
    }

    [Fact]
    public async Task A_neighbour_that_times_out_fails_the_request_at_once_as_the_client_s_timeout_does()
    {
        shop.TimeOut("GET", "/items/1");

        var timedOut = await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync("https://shop.test/items/1").WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", timedOut.Message);
        Assert.IsType<TimeoutException>(timedOut.InnerException);
        Assert.Empty(shop.Unanswered);
    }

    [Fact]
    public async Task A_request_that_timed_out_is_recorded_and_traced_as_sent()
    {
        shop.TimeOut("GET", "/items/1");

        await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync("https://shop.test/items/1"));

        Assert.Equal(["/items/1"], shop.Requests("GET", "/items/1").Select(r => r.Path));
        var sent = Assert.Single(trace.Records);
        Assert.Equal(("out", "http", "GET /items/{id}"), (sent.Dir, sent.Kind, sent.Name));
    }

    [Fact]
    public async Task A_timeout_takes_the_requests_its_query_takes()
    {
        shop.Reply("GET", "/items/1", 200, new { id = "1" });
        shop.TimeOut("GET", "/items/1", new Query { ["view"] = "full" });

        await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync("https://shop.test/items/1?view=full&lang=en"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("https://shop.test/items/1?view=short")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("https://shop.test/items/1")).StatusCode);
    }

    [Fact]
    public async Task A_timeout_is_declared_by_a_template_too()
    {
        shop.TimeOut("GET", "/items/{id}");

        await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync("https://shop.test/items/9"));
    }

    [Fact]
    public async Task A_timeout_wins_over_a_hold()
    {
        shop.Hold("GET", "/items/1");
        shop.TimeOut("GET", "/items/1");

        await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync("https://shop.test/items/1").WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Empty(shop.Unreleased);
    }

    [Fact]
    public async Task Heal_lifts_every_timeout_and_keeps_the_replies()
    {
        shop.Reply("GET", "/items/2", 200, new { id = "2" });
        shop.TimeOut("GET", "/items/1");
        shop.TimeOut("GET", "/items/2", new Query { ["view"] = "full" });
        shop.Reply("GET", "/items/1", 200, new { id = "1" });

        await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync("https://shop.test/items/1"));
        shop.Heal();

        Assert.Equal("1", (await client.GetFromJsonAsync<Item>("https://shop.test/items/1"))!.Id);
        Assert.Equal("2", (await client.GetFromJsonAsync<Item>("https://shop.test/items/2?view=full"))!.Id);
    }

    [Fact]
    public async Task An_answer_is_worked_out_from_each_request()
    {
        shop.ReplyWith("GET", "/items/{id}", r => (200, new { id = r.Path.Split('/')[^1] }), new Dictionary<string, string> { ["X-Shard"] = "own" });

        foreach (var id in new[] { "1", "2" })
        {
            var answer = await client.GetAsync($"https://shop.test/items/{id}");
            Assert.Equal(id, (await answer.Content.ReadFromJsonAsync<Item>())!.Id);
            Assert.Equal("own", Assert.Single(answer.Headers.GetValues("X-Shard")));
        }
        Assert.Empty(shop.Violations);
    }

    [Fact]
    public async Task An_answer_may_change_with_what_reached_the_neighbour()
    {
        shop.ReplyWith("GET", "/items/1", _ =>
        {
            if (shop.Requests("GET", "/items/1").Count == 1)
            {
                return (404, new { code = 7, message = "not yet" });
            }
            return (200, new { id = "1" });
        });

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("https://shop.test/items/1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("https://shop.test/items/1")).StatusCode);
        Assert.Empty(shop.Violations);
    }

    [Fact]
    public async Task An_answer_worked_out_is_a_reply_naming_no_query()
    {
        shop.ReplyWith("GET", "/items/1", _ => (200, new { id = "worked out" }));
        shop.ReplyTo("GET", "/items/1", new Query { ["view"] = "full" }, 200, new { id = "full" });

        Assert.Equal("worked out", (await client.GetFromJsonAsync<Item>("https://shop.test/items/1?view=short"))!.Id);
        Assert.Equal("full", (await client.GetFromJsonAsync<Item>("https://shop.test/items/1?view=full"))!.Id);
        shop.Reply("GET", "/items/1", 200, new { id = "declared" });
        Assert.Equal("declared", (await client.GetFromJsonAsync<Item>("https://shop.test/items/1"))!.Id);
    }

    [Fact]
    public async Task The_contract_checks_an_answer_worked_out()
    {
        shop.ReplyWith("GET", "/items/{id}", _ => (200, new { id = 7 }));

        await client.GetAsync("https://shop.test/items/7");

        Assert.Contains("/items/7", Assert.Single(shop.Violations));
    }

    sealed record Item(string Id);

    sealed record Failure(int Code, string Message);
}
