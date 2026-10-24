namespace Specrig.SurfaceTests;

public class SurfaceTests
{
    static string Sample(string path) => Path.Combine(Repo(), "samples", path);

    static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Specrig.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("no Specrig.slnx above the tests");
    }

    [Fact]
    public async Task A_web_api_serves_its_routes_and_its_grpc_methods_and_calls_its_neighbours()
    {
        var shop = await Surface.Extract(Sample("shop/src/Shop.csproj"), "shop");

        Assert.Equal(["/shop.Orders/Get", "GET /orders/{id}"], shop.Serves);
        Assert.Equal(["/customers.Customers/OfOrder", "GET /invoices/{}"], shop.Calls);
        Assert.Equal(["GET /invoices/{}"], Assert.Single(shop.Clients, c => c.Key == "Shop.Billing").Value);
        Assert.Equal(["Billing:BaseUrl", "ConnectionStrings:Shop", "Customers:Address"], shop.Settings);
    }

    [Fact]
    public async Task A_controller_serves_the_routes_its_base_classes_and_overridden_methods_declare()
    {
        var gateway = await Surface.Extract(Path.Combine(Repo(), "test", "fixtures", "gateway", "src", "Gateway.csproj"), "gateway");

        Assert.Equal(
            ["GET /admin/accounts/{accountId}", "GET /api/Letters", "GET /healthz", "GET /letters/count", "POST /accounts/login", "POST /api/Letters/{to}"],
            gateway.Serves);
        Assert.Equal(["/customer.Accounts/Get", "/customer.Sessions/Summon", "/notification.Mail/Send"], gateway.Calls);
        Assert.Equal("Gateway.Customer.Sessions", gateway.Descriptors["customer.Sessions"]);
    }

    [Fact]
    public async Task A_worker_publishes_and_consumes_by_routing_key()
    {
        var reminders = await Surface.Extract(Sample("reminders/src/Reminders.csproj"), "reminders");

        Assert.Equal(["reminders.sent"], reminders.Publishes);
        Assert.Equal(["invoices.paid"], reminders.Consumes);
        Assert.Equal(["GET /invoices", "POST /reminders"], reminders.Calls);
        Assert.Empty(reminders.Serves);
        Assert.Equal(["Billing:BaseUrl", "ConnectionStrings:Rabbit"], reminders.Settings);
        Assert.Equal(["Reminders.InvoicesPaid"], reminders.Consumers);
    }

    [Fact]
    public async Task The_record_says_first_that_it_is_specrigs()
    {
        var reminders = await Surface.Extract(Sample("reminders/src/Reminders.csproj"), "reminders");

        Assert.Equal(["specrig: surface/1", "service: reminders"], reminders.Encode().Split('\n')[1..3]);
    }
}
