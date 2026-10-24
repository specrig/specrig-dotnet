using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Specrig.RedisTests;

public class RedisTests
{
    readonly Redis cache = new("ConnectionStrings:Cache");

    async Task<IConnectionMultiplexer> Service()
    {
        var address = await cache.Provision("shop", CancellationToken.None);
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(address));
        cache.Configure(services);
        return services.BuildServiceProvider().GetRequiredService<IConnectionMultiplexer>();
    }

    [Fact]
    public async Task A_case_starts_from_an_empty_cache()
    {
        var database = (await Service()).GetDatabase();
        await database.StringSetAsync("order:7", "placed");

        await cache.Reset(CancellationToken.None);

        Assert.False(await database.KeyExistsAsync("order:7"));
    }

    [Fact]
    public async Task A_cache_that_fails_refuses_every_command_of_the_service()
    {
        var database = (await Service()).GetDatabase();
        await database.StringSetAsync("order:7", "placed");

        cache.FailWith(null);

        await Assert.ThrowsAsync<RedisConnectionException>(() => database.StringGetAsync("order:7"));
    }

    [Fact]
    public async Task An_address_that_leads_nowhere_fails_without_its_password()
    {
        var nowhere = new Redis("ConnectionStrings:Cache") { Address = "nowhere.invalid:6379,password=s3cret,connectTimeout=500" };
        await nowhere.Provision("shop", CancellationToken.None);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => nowhere.Reset(CancellationToken.None));

        Assert.StartsWith("redis at nowhere.invalid:6379:", refused.Message);
        Assert.DoesNotContain("s3cret", refused.ToString());
    }
}
