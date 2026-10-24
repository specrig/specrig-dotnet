using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shop;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<Billing>(client => client.BaseAddress = new Uri(builder.Configuration["Billing:BaseUrl"]!));
builder.Services.AddDbContext<ShopDb>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("Shop")));
builder.Services.AddScoped<IOrders, Orders>();
builder.Services.AddProblemDetails();
builder.Services.AddGrpc();
builder.Services.AddGrpcClient<Shop.Customers.Customers.CustomersClient>(options => options.Address = new Uri(builder.Configuration["Customers:Address"]!));

var app = builder.Build();

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = error => error is NpgsqlException or { InnerException: NpgsqlException }
        ? StatusCodes.Status503ServiceUnavailable
        : StatusCodes.Status500InternalServerError,
});

app.MapGet("/orders/{id:int}", async (int id, IOrders orders, Billing billing, CancellationToken cancel) =>
{
    if (await orders.Find(id, cancel) is null)
    {
        return Results.Problem(type: "order_not_found", title: $"no order {id}", statusCode: StatusCodes.Status404NotFound);
    }
    var invoice = await billing.InvoiceOf(id, cancel);
    return Results.Ok(new OrderView(id, invoice.Amount));
});

app.MapGrpcService<OrdersService>();

app.Run();

public partial class Program;
