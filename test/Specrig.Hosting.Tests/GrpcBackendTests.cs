using System.IO.Compression;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Compression;
using Shop.Customers;

namespace Specrig.Hosting.Tests;

public class GrpcBackendTests
{
    readonly GrpcBackend customers = new("https://customers.shop.test", Shop.Customers.Customers.Descriptor);
    readonly GrpcChannel channel;
    readonly Customers.CustomersClient client;

    public GrpcBackendTests()
    {
        ((IStandIn)customers).Attach("Customers", new Trace(), () => "");
        channel = GrpcChannel.ForAddress(customers.Origin, new GrpcChannelOptions { HttpHandler = new StandIns([customers]).Handler() });
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

    [Theory]
    [InlineData("gzip")]
    [InlineData("identity")]
    public async Task A_request_in_an_encoding_the_neighbour_reads_is_answered(string encoding)
    {
        customers.Reply("OfOrder", new Customer { Name = "Ann" });

        var customer = await client.OfOrderAsync(new OrderRef { Order = 7 }, new Metadata { { "grpc-internal-encoding-request", encoding } });

        Assert.Equal("Ann", customer.Name);
        Assert.Equal(7, Assert.Single(customers.Requests<OrderRef>("OfOrder")).Order);
        Assert.Empty(((IStandIn)customers).Failures());
    }

    [Fact]
    public async Task A_request_compressed_in_an_encoding_the_neighbour_does_not_read_is_unimplemented_and_named()
    {
        var deflating = GrpcChannel.ForAddress(customers.Origin, new GrpcChannelOptions
        {
            HttpHandler = new StandIns([customers]).Handler(),
            CompressionProviders = [new DeflateCompressionProvider(CompressionLevel.Fastest)],
        });
        customers.Reply("OfOrder", new Customer { Name = "Ann" });

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await new Customers.CustomersClient(deflating).OfOrderAsync(new OrderRef { Order = 7 }, new Metadata { { "grpc-internal-encoding-request", "deflate" } }));

        Assert.Equal(StatusCode.Unimplemented, refused.StatusCode);
        Assert.Contains("/customers.Customers/OfOrder came compressed with deflate, which Customers does not read", Assert.Single(((IStandIn)customers).Failures()));
        Assert.Empty(customers.Requests<OrderRef>("OfOrder"));
    }

    [Fact]
    public async Task A_request_that_is_no_message_of_the_method_is_named()
    {
        var bytes = Marshallers.Create<byte[]>(payload => payload, payload => payload);
        var raw = new Method<byte[], byte[]>(MethodType.Unary, "customers.Customers", "OfOrder", bytes, bytes);
        customers.Reply("OfOrder", new Customer { Name = "Ann" });

        var refused = await Assert.ThrowsAsync<RpcException>(async () => await channel.CreateCallInvoker().AsyncUnaryCall(raw, null, new CallOptions(), [0xFF]));

        Assert.Equal(StatusCode.Internal, refused.StatusCode);
        Assert.Contains("/customers.Customers/OfOrder sent no readable OrderRef", Assert.Single(((IStandIn)customers).Failures()));
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
