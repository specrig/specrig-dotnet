using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Shop.Customers;
using Shop.Grpc;

namespace Shop;

public sealed class OrdersService(IOrders orders, Billing billing, Customers.Customers.CustomersClient customers) : Grpc.Orders.OrdersBase
{
    public override async Task<OrderReply> Get(Grpc.OrderRef request, ServerCallContext context)
    {
        if (await orders.Find(request.Id, context.CancellationToken) is null)
        {
            throw new Google.Rpc.Status
            {
                Code = (int)Code.NotFound,
                Message = $"no order {request.Id}",
                Details = { Any.Pack(new ErrorInfo { Reason = "ORDER_NOT_FOUND", Domain = "shop" }) },
            }.ToRpcException();
        }
        var invoice = await billing.InvoiceOf(request.Id, context.CancellationToken);
        var customer = await customers.OfOrderAsync(new Customers.OrderRef { Order = request.Id }, cancellationToken: context.CancellationToken);
        return new OrderReply { Id = request.Id, ToPay = invoice.Amount, Customer = customer.Name };
    }
}
