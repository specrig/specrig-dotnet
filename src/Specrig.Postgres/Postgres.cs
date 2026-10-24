using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Specrig;

public sealed class Postgres(string setting) : IResource, IDisposable
{
    static readonly ConcurrentDictionary<string, Lazy<Task<PostgresServer>>> servers = new();

    static readonly MethodInfo configureDbContext = typeof(EntityFrameworkServiceCollectionExtensions).GetMethods()
        .Single(m => m.Name == nameof(EntityFrameworkServiceCollectionExtensions.ConfigureDbContext)
            && m.GetParameters()[1].ParameterType == typeof(Action<DbContextOptionsBuilder>));

    readonly Fault fault = new();
    readonly List<Type> contexts = [];
    PostgresServer? server;
    Gate? gate;
    bool ready;
    bool refusing;

    public string Setting { get; } = setting;

    public string? Address { get; init; }

    public string Image { get; init; } = "postgres:17-alpine";

    public IReadOnlyList<string> Keep { get; init; } = [];

    PostgresServer Server => server ?? throw new InvalidOperationException("postgres is not provisioned: the service has not booted");

    public void FailWith(Exception? error)
    {
        fault.Error = error ?? new NpgsqlException("the database refuses connections", new SocketException((int)SocketError.ConnectionRefused));
        refusing = error is null;
        if (refusing && ready)
        {
            gate?.Close();
        }
    }

    public async Task<string> Provision(string service, CancellationToken cancel)
    {
        var name = Named(service);
        var given = Address ?? Environment.GetEnvironmentVariable($"{name.ToUpperInvariant()}_TEST_POSTGRES");
        server = await servers.GetOrAdd($"{given ?? Image}\n{name}", _ => new(() => PostgresServer.Start(name, given, Image))).Value;
        var address = new NpgsqlConnectionStringBuilder(server.Address);
        gate ??= new Gate(address.Host!, address.Port);
        address.Host = "127.0.0.1";
        address.Port = gate.Port;
        return address.ConnectionString;
    }

    public void Configure(IServiceCollection services)
    {
        IInterceptor[] interceptors = [new Refusing(fault), new Failing(fault)];
        var registered = services.Select(d => d.ServiceType).Select(type => type switch
        {
            _ when type.IsSubclassOf(typeof(DbContext)) => type,
            { IsGenericType: true } when type.GetGenericTypeDefinition() == typeof(IDbContextFactory<>) => type.GetGenericArguments()[0],
            _ => null,
        });
        foreach (var type in registered.OfType<Type>().Distinct().ToList())
        {
            contexts.Add(type);
            configureDbContext.MakeGenericMethod(type).Invoke(null,
                [services, (Action<DbContextOptionsBuilder>)(options => options.AddInterceptors(interceptors)), ServiceLifetime.Scoped]);
        }
    }

    public Task Migrate(IServiceProvider services, CancellationToken cancel) => Server.Once(async () =>
    {
        using var _ = fault.Suspend();
        await using var scope = services.CreateAsyncScope();
        foreach (var type in contexts)
        {
            await using var context = Context(scope.ServiceProvider, type);
            if (context.Database.GetMigrations().Any())
            {
                await context.Database.MigrateAsync(cancel);
            }
            else
            {
                await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(cancel);
            }
        }
        await Server.Discover(Keep, cancel);
    });

    // A fault a step set before the boot waits for it: the migration goes through the gate too.
    public async Task Reset(CancellationToken cancel)
    {
        await Server.Reset(cancel);
        ready = true;
        if (refusing)
        {
            gate?.Close();
        }
    }

    public void Dispose() => gate?.Dispose();

    // A context the container builds itself is the scope's to dispose, so the caller gets one of its own.
    static DbContext Context(IServiceProvider services, Type type)
    {
        var factory = services.GetService(typeof(IDbContextFactory<>).MakeGenericType(type));
        var made = factory?.GetType().GetMethod(nameof(IDbContextFactory<>.CreateDbContext), Type.EmptyTypes)?.Invoke(factory, null);
        return (DbContext)(made ?? ActivatorUtilities.CreateInstance(services, type));
    }

    static string Named(string service) => string.Concat(service.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));
}
