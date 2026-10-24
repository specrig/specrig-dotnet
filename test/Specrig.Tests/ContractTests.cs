using System.Text.Json.Nodes;

namespace Specrig.Tests;

public class ContractTests
{
    static readonly string file = Path.Combine(AppContext.BaseDirectory, "fixtures", "shop.yaml");

    readonly Contract shop = new(file, "shop", "Error");

    static JsonNode? Json(string text) => JsonNode.Parse(text);

    [Fact]
    public void An_answer_the_neighbour_can_give_passes() =>
        Assert.Empty(shop.Response("GET", "/items/1", 404, Json("""{"code": 404, "message": "not found"}""")));

    [Fact]
    public void An_answer_in_another_shape_than_the_contracts_is_refused() =>
        Assert.NotEmpty(shop.Response("GET", "/items/1", 404, Json("""{"code": "not_found"}""")));

    [Fact]
    public void A_status_the_route_does_not_answer_is_refused_a_failure_of_the_server_is_not()
    {
        Assert.Equal(["GET /items/1 answered 418: shop does not answer 418 here"], shop.Response("GET", "/items/1", 418, new JsonObject()));
        Assert.Empty(shop.Response("GET", "/items/1", 503, Json("""{"code": 503, "message": "unavailable"}""")));
        Assert.NotEmpty(shop.Response("GET", "/items/1", 503, Json("""{"reason": "unavailable"}""")));
    }

    [Fact]
    public void Without_a_failure_schema_a_failure_the_route_does_not_document_is_refused() =>
        Assert.NotEmpty(new Contract(file, "shop").Response("GET", "/items/1", 503, Json("""{"code": 503, "message": "x"}""")));

    [Fact]
    public void A_request_body_the_neighbour_would_not_accept_is_refused()
    {
        Assert.NotEmpty(shop.Request("POST", "/items/new", Json("""{"count": 1}""")));
        Assert.Empty(shop.Request("POST", "/items/new", Json("""{"name": "box", "count": 1}""")));
    }

    [Fact]
    public void A_route_the_neighbour_does_not_serve_is_refused() =>
        Assert.Equal(["DELETE /items/1: shop serves no such route"], shop.Request("DELETE", "/items/1", null));

    [Fact]
    public void The_template_with_the_most_constant_segments_takes_the_request()
    {
        Assert.Empty(shop.Response("GET", "/items/new", 200, Json("""{"draft": true}""")));
        Assert.NotEmpty(shop.Response("GET", "/items/new", 200, Json("""{"id": "new"}""")));
        Assert.Equal("GET /items/{id}", shop.Name("GET", "/items/1"));
    }
}
