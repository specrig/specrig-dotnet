using System.Net;
using System.Net.Http.Json;
using Grpc.Core;
using Reqnroll;
using Shop.Customers;
using Shop.Grpc;
using Shop.Specs.Internal;
using Specrig;

namespace Shop.Specs.Steps;

[Binding]
public sealed class OrderSteps(Harness harness)
{
    HttpResponseMessage? opened;
    OrderReply? shown;
    RpcException? refused;

    [Given("customers knows order {int} as {word}")]
    public void Customer(int order, string name) => harness.Customers.Reply("OfOrder", new Customer { Name = name.TrimEnd('\'', 's') });

    [When("the app asks for order {int}")]
    public async Task Ask(int order)
    {
        try
        {
            shown = await harness.Grpc<Grpc.Orders.OrdersClient>().GetAsync(new Grpc.OrderRef { Id = order });
        }
        catch (RpcException e)
        {
            refused = e;
        }
    }

    [Then("it shows {int} to pay for {word}")]
    public void Shows(int amount, string name)
    {
        Assert.Equal((amount, name), (shown!.ToPay, shown.Customer));
        Assert.Equal(7, Assert.Single(harness.Customers.Requests<Customers.OrderRef>("OfOrder")).Order);
    }

    [Then("it is told there is no such order")]
    public void NoSuchOrderOverGrpc() => Assert.Equal(StatusCode.NotFound, refused!.StatusCode);

    [Given("order {int} is placed")]
    public async Task Placed(int order)
    {
        await using var scope = harness.Scope();
        await scope.Get<IOrders>().Save(new Order(order), CancellationToken.None);
    }

    [Given("billing holds an invoice of {int} for order {int}")]
    public void Invoice(int amount, int order) =>
        harness.Billing.Reply("GET", $"/invoices/{order}", 200, new { order, amount, dueOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14) });

    [Given("the shop's database is down")]
    public void DatabaseDown() => harness.Database.FailWith(null);

    [When("the customer opens order {int}")]
    public async Task Open(int order) => opened = await harness.Http.GetAsync($"/orders/{order}");

    [Then("they see {int} to pay")]
    public async Task ToPay(int amount)
    {
        Assert.Equal(HttpStatusCode.OK, opened!.StatusCode);
        Assert.Equal(amount, (await opened.Content.ReadFromJsonAsync<OrderView>())!.ToPay);
    }

    [Then("they are told there is no such order")]
    public void NoSuchOrder() => Assert.Equal(HttpStatusCode.NotFound, opened!.StatusCode);

    [Then("they are told to come back later")]
    public void ComeBack() => Assert.Equal(HttpStatusCode.ServiceUnavailable, opened!.StatusCode);
}
