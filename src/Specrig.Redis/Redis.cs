using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Specrig;

public sealed class Redis(string setting) : IResource
{
    static readonly ConcurrentDictionary<string, Lazy<Task<RedisServer>>> servers = new();

    Exception? fault;
    RedisServer? server;

    public string Setting { get; } = setting;

    public string? Address { get; init; }

    public string Image { get; init; } = "redis:8-alpine";

    public void FailWith(Exception? error) =>
        fault = error ?? new RedisConnectionException(
            ConnectionFailureType.UnableToConnect, CommandFlags.None, "the cache refuses connections", null, CommandStatus.Unknown);

    public async Task<string> Provision(string service, CancellationToken cancel)
    {
        var name = string.Concat(service.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));
        var given = Address ?? Environment.GetEnvironmentVariable($"{name}_TEST_REDIS");
        server = await servers.GetOrAdd(given ?? Image, _ => new(() => RedisServer.Start(name, given, Image))).Value;
        return server.Address;
    }

    public void Configure(IServiceCollection services)
    {
        foreach (var registered in services.Where(d => d.ServiceType == typeof(IConnectionMultiplexer) && !d.IsKeyedService).ToList())
        {
            services.Remove(registered);
            services.AddSingleton(provider =>
                (IConnectionMultiplexer)Proxy.Failing(typeof(IConnectionMultiplexer), Built(registered, provider), () => fault));
        }
    }

    public Task Migrate(IServiceProvider services, CancellationToken cancel) => Task.CompletedTask;

    public Task Reset(CancellationToken cancel) =>
        (server ?? throw new InvalidOperationException("redis is not provisioned: the service has not booted")).Flush();

    static object Built(ServiceDescriptor registered, IServiceProvider provider) =>
        registered.ImplementationInstance
        ?? registered.ImplementationFactory?.Invoke(provider)
        ?? ActivatorUtilities.CreateInstance(provider, registered.ImplementationType!);
}

sealed class RedisServer
{
    readonly ConfigurationOptions admin;

    RedisServer(string address)
    {
        Address = address;
        admin = ConfigurationOptions.Parse(address);
        admin.AllowAdmin = true;
    }

    public string Address { get; }

    // Starting redis costs less than a second, so the container is the run's own rather than kept.
    public static async Task<RedisServer> Start(string service, string? given, string image)
    {
        if (given is not null)
        {
            return new RedisServer(given);
        }
        var container = new RedisBuilder(image).WithLabel("specrig", "redis").Build();
        try
        {
            await container.StartAsync();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"redis: no address is given and no container started — set {service}_TEST_REDIS, or start Docker: {e.Message}", e);
        }
        Rig.Ending(async () => await container.DisposeAsync());
        return new RedisServer(container.GetConnectionString());
    }

    public async Task Flush()
    {
        try
        {
            await using var connection = await ConnectionMultiplexer.ConnectAsync(admin);
            foreach (var endpoint in connection.GetEndPoints())
            {
                await connection.GetServer(endpoint).FlushDatabaseAsync(admin.DefaultDatabase ?? 0);
            }
        }
        catch (RedisConnectionException e)
        {
            var where = admin.EndPoints.Select(endpoint => endpoint is System.Net.DnsEndPoint dns ? $"{dns.Host}:{dns.Port}" : endpoint.ToString());
            throw new InvalidOperationException($"redis at {string.Join(", ", where)}: {e.Message}", e);
        }
    }
}
