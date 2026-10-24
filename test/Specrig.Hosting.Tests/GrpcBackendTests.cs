using Grpc.Core;
using Grpc.Net.Client;
using Shop.Customers;

namespace Specrig.Hosting.Tests;

public class GrpcBackendTests
{
    readonly GrpcBackend customers = new("https://customers.shop.test", Shop.Customers.Customers.Descriptor);
    readonly Customers.CustomersClient client;

    public GrpcBackendTests()
    {
        ((IStandIn)customers).Attach("Customers", new Trace(), () => "");
        var channel = GrpcChannel.ForAddress(customers.Origin, new GrpcChannelOptions { HttpHandler = new StandIns([customers]).Handler() });
        client = new Customers.CustomersClient(channel);
    }

    [Fact]
    public async Task A_neighbour_refuses_with_the_status_and_the_reason_a_step_gave()
    {
        customers.Fail("OfOrder", StatusCode.NotFound, "CUSTOMER_GONE");

        var refused = await Assert.ThrowsAsync<RpcException>(async () => await client.OfOrderAsync(new OrderRef { Order = 7 }));

        Assert.Equal((StatusCode.NotFound, "CUSTOMER_GONE"), (refused.StatusCode, refused.GetRpcStatus()?.GetDetail<Google.Rpc.ErrorInfo>()?.Reason));
    }

    [Fact]
    public async Task A_call_no_step_answered_is_unimplemented_and_named()
    {
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await client.OfOrderAsync(new OrderRef { Order = 7 }));

        Assert.Equal(StatusCode.Unimplemented, refused.StatusCode);
        Assert.Equal(["Customers was called where no step declared its answer: /customers.Customers/OfOrder"], ((IStandIn)customers).Failures());
    }

    [Fact]
    public async Task An_answer_of_another_message_than_the_method_returns_is_a_violation()
    {
        customers.Reply("OfOrder", new OrderRef { Order = 7 });

        await client.OfOrderAsync(new OrderRef { Order = 7 });

        Assert.Contains("answered a OrderRef where OfOrder answers a Customer", Assert.Single(((IStandIn)customers).Failures()));
    }

    [Fact]
    public async Task A_neighbour_answers_for_every_service_it_serves()
    {
        var warehouse = new GrpcBackend("https://warehouse.shop.test", Shop.Grpc.Orders.Descriptor, Stock.Stock.Descriptor);
        ((IStandIn)warehouse).Attach("Warehouse", new Trace(), () => "");
        var channel = GrpcChannel.ForAddress(warehouse.Origin, new GrpcChannelOptions { HttpHandler = new StandIns([warehouse]).Handler() });
        warehouse.Reply("Orders.Get", new Shop.Grpc.OrderReply { Id = 7 });
        warehouse.Reply("stock.Stock.Get", new Stock.Level { Count = 3 });

        var order = await new Shop.Grpc.Orders.OrdersClient(channel).GetAsync(new Shop.Grpc.OrderRef { Id = 7 });
        var level = await new Stock.Stock.StockClient(channel).GetAsync(new Stock.Item { Id = 5 });

        Assert.Equal((7, 3), (order.Id, level.Count));
        Assert.Equal(5, Assert.Single(warehouse.Requests<Stock.Item>("Stock.Get")).Id);
        Assert.Empty(((IStandIn)warehouse).Failures());
    }

    [Fact]
    public void A_method_two_services_share_is_named_with_its_service()
    {
        var warehouse = new GrpcBackend("https://warehouse.shop.test", Shop.Grpc.Orders.Descriptor, Stock.Stock.Descriptor);

        var refused = Assert.Throws<ArgumentException>(() => warehouse.Reply("Get", new Stock.Level()));

        Assert.Equal("Get is one of shop.Orders.Get, stock.Stock.Get: name the service", refused.Message);
    }
}
