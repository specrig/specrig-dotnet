using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shop;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Specrig.Hosting.Tests;

public class HarnessTests : IDisposable
{
    readonly ShopHarness harness = new();

    public void Dispose()
    {
        harness.Dispose();
        GC.SuppressFinalize(this);
    }

    class ShopHarness : Harness<Program>
    {
        public override string Service => "shop";

        public Backend Billing { get; } = new("https://billing.shop.test")
        {
            Setting = "Billing:BaseUrl",
            Contract = Path.Combine(AppContext.BaseDirectory, "fixtures", "billing.yaml"),
            Failure = "Error",
        };

        public virtual Postgres Database { get; } = new("ConnectionStrings:Shop");

        public async Task Place(int order)
        {
            await using var scope = Scope();
            await scope.Get<IOrders>().Save(new Order(order), CancellationToken.None);
        }

        public async Task<Order?> Find(int order)
        {
            await using var scope = Scope();
            return await scope.Get<IOrders>().Find(order, CancellationToken.None);
        }
    }

    sealed class Granting : ShopHarness
    {
        protected override Grants Allow => [typeof(IOrders)];
    }

    sealed class RunningWhatShopDoesNot : ShopHarness
    {
        protected override Workers Running => [typeof(Orders)];
    }

    sealed class Nowhere : ShopHarness
    {
        public override Postgres Database { get; } = new("ConnectionStrings:Shop")
        {
            Address = "Host=nowhere.invalid;Port=5432;Username=shop;Password=s3cret;Timeout=2",
        };
    }

    sealed class RawQuery(IConfiguration configuration)
    {
        public async Task RunAsync()
        {
            await using var connection = new NpgsqlConnection(configuration.GetConnectionString("Shop"));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync();
        }
    }

    sealed class Leaky
    {
        public async Task RunAsync()
        {
            using var client = new HttpClient();
            await client.GetAsync("http://leak.invalid/orders");
        }
    }

    sealed class NoOrders : IOrders
    {
        public Task<Order?> Find(int id, CancellationToken cancel) => Task.FromResult<Order?>(null);

        public Task Save(Order order, CancellationToken cancel) => Task.CompletedTask;
    }

    [Fact]
    public void The_harness_answers_a_setting_over_appsettings()
    {
        using var scope = harness.Scope();
        var configuration = scope.Get<IConfiguration>();

        Assert.Equal("https://billing.shop.test", configuration["Billing:BaseUrl"]);
        Assert.Equal($"shop_test_{Environment.ProcessId}", new NpgsqlConnectionStringBuilder(configuration["ConnectionStrings:Shop"]).Database);
    }

    [Fact]
    public void The_host_is_named_after_the_service_not_the_suite()
    {
        using var scope = harness.Scope();

        Assert.Equal("Shop", scope.Get<IHostEnvironment>().ApplicationName);
    }

    [Fact]
    public async Task A_call_no_step_answered_is_a_failure_named()
    {
        await harness.Place(7);

        Assert.Equal(HttpStatusCode.InternalServerError, (await harness.Http.GetAsync("/orders/7")).StatusCode);

        Assert.Equal(["Billing was called where no step declared its answer: GET /invoices/7"], harness.Failures());
    }

    [Fact]
    public async Task An_answer_the_contract_does_not_allow_is_a_failure()
    {
        await harness.Place(7);
        harness.Billing.Reply("GET", "/invoices/7", 200, new { order = 7, amount = 120 });

        await harness.Http.GetAsync("/orders/7");

        Assert.Contains("does not allow", Assert.Single(harness.Failures()));
    }

    [Fact]
    public async Task A_request_is_traced_by_the_route_it_matched_and_its_refusal()
    {
        harness.Trace.Act("Action");

        Assert.Equal(HttpStatusCode.NotFound, (await harness.Http.GetAsync("/orders/8")).StatusCode);

        var received = Assert.Single(harness.Trace.Records);
        Assert.Equal(("in", "http", "GET /orders/{id}", "HTTP 404 order_not_found"), (received.Dir, received.Kind, received.Name, received.Error));
    }

    [Fact]
    public async Task A_grpc_call_is_traced_by_its_method_its_message_and_its_refusal()
    {
        harness.Trace.Act("Action");

        var refused = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
            await harness.Grpc<Shop.Grpc.Orders.OrdersClient>().GetAsync(new Shop.Grpc.OrderRef { Id = 8 }));

        Assert.Equal(Grpc.Core.StatusCode.NotFound, refused.StatusCode);
        var received = Assert.Single(harness.Trace.Records);
        Assert.Equal(("in", "grpc", "/shop.Orders/Get", "NotFound order_not_found"), (received.Dir, received.Kind, received.Name, received.Error));
        Assert.Equal(8, received.Request!["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_request_past_the_stand_ins_is_a_failure_named()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => harness.Run<Leaky>());

        Assert.Equal(["requests to an origin ShopHarness does not name: GET http://leak.invalid/orders, past the stand-ins"], harness.Failures());
    }

    [Fact]
    public void A_seam_the_harness_does_not_grant_is_refused()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => harness.Replace<IOrders>(new NoOrders()));

        Assert.Equal("IOrders is not granted: Allow in ShopHarness does not name it", refused.Message);
    }

    [Fact]
    public void A_granted_seam_is_what_the_service_gets()
    {
        using var granting = new Granting();
        var orders = new NoOrders();
        granting.Replace<IOrders>(orders);

        using var scope = granting.Scope();
        Assert.Same(orders, scope.Get<IOrders>());
    }

    [Fact]
    public void Running_names_only_what_the_service_runs()
    {
        using var running = new RunningWhatShopDoesNot();

        var refused = Assert.Throws<InvalidOperationException>(() => running.Scope());

        Assert.Equal("Running names Orders, which shop does not run", refused.Message);
    }

    [Fact]
    public async Task Every_case_starts_from_an_empty_database()
    {
        await harness.Place(7);
        harness.Dispose();

        using var next = new ShopHarness();

        Assert.Null(await next.Find(7));
    }

    [Fact]
    public async Task A_database_that_fails_refuses_the_service_until_the_next_case()
    {
        await harness.Place(7);
        harness.Database.FailWith(null);
        harness.Trace.Act("Action");

        var answer = await harness.Http.GetAsync("/orders/7");

        Assert.True(answer.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"{answer.StatusCode}: {string.Join("\n", harness.Logs.GetSnapshot().Select(r => r.Exception?.ToString()).OfType<string>())}");
        Assert.Equal(("GET /orders/{id}", "HTTP 503"), (harness.Trace.Records[^1].Name, harness.Trace.Records[^1].Error));
        harness.Dispose();
        using var next = new ShopHarness();
        await next.Place(8);
        Assert.NotNull(await next.Find(8));
    }

    [Fact]
    public async Task A_database_that_fails_refuses_a_client_of_npgsql_s_own()
    {
        await harness.Run<RawQuery>();

        harness.Database.FailWith(null);

        await Assert.ThrowsAsync<NpgsqlException>(() => harness.Run<RawQuery>());
        var refused = await Assert.ThrowsAsync<NpgsqlException>(() => harness.Run<RawQuery>());
        Assert.Equal(SocketError.ConnectionRefused, Assert.IsType<SocketException>(refused.InnerException).SocketErrorCode);
    }

    [Fact]
    public async Task A_context_the_container_makes_only_through_a_factory_is_migrated()
    {
        using var database = new Postgres("ConnectionStrings:Shop");
        var address = await database.Provision("factory", CancellationToken.None);
        var services = new ServiceCollection();
        services.AddDbContextFactory<ShopDb>(options => options.UseNpgsql(address));
        database.Configure(services);
        await using var provider = services.BuildServiceProvider();

        await database.Migrate(provider, CancellationToken.None);

        await using var db = await provider.GetRequiredService<IDbContextFactory<ShopDb>>().CreateDbContextAsync();
        Assert.Empty(await db.Orders.ToListAsync());
    }

    [Fact]
    public async Task A_run_sweeps_what_a_killed_run_left_and_nothing_else()
    {
        var address = new NpgsqlConnectionStringBuilder(await new Postgres("ConnectionStrings:Sweep").Provision("sweep", CancellationToken.None));
        var admin = new NpgsqlConnectionStringBuilder(address.ConnectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        async Task Execute(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        var twoHoursAgo = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
        foreach (var (name, mark) in new[] { ("sweep_test_1", $"specrig {twoHoursAgo}"), ("sweep_test_2", $"specrig {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"), ("sweep_test_kept", $"specrig {twoHoursAgo}"), ("sweep_test_3", "kept by hand") })
        {
            await Execute($"DROP DATABASE IF EXISTS \"{name}\"");
            await Execute($"CREATE DATABASE \"{name}\"");
            await Execute($"COMMENT ON DATABASE \"{name}\" IS '{mark}'");
        }

        await PostgresServer.Start("sweep", admin.ConnectionString, "unused");

        await using var list = new NpgsqlCommand("SELECT datname FROM pg_database WHERE datname LIKE 'sweep_test_%'", connection);
        var left = new List<string>();
        await using (var reader = await list.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                left.Add(reader.GetString(0));
            }
        }
        Assert.DoesNotContain("sweep_test_1", left);
        Assert.Contains("sweep_test_2", left);
        Assert.Contains("sweep_test_kept", left);
        Assert.Contains("sweep_test_3", left);
        foreach (var name in new[] { "sweep_test_2", "sweep_test_kept", "sweep_test_3" })
        {
            await Execute($"DROP DATABASE IF EXISTS \"{name}\"");
        }
    }

    [Fact]
    public void An_address_that_leads_nowhere_fails_the_boot_without_its_password()
    {
        using var nowhere = new Nowhere();

        var refused = Assert.ThrowsAny<Exception>(() => nowhere.Scope());

        Assert.StartsWith("postgres at nowhere.invalid:5432:", refused.Message);
        Assert.DoesNotContain("s3cret", refused.ToString());
    }
}
