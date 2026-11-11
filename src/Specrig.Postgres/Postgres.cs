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

public enum Write
{
    Insert,
    Update,
    Delete,
}

sealed record Refusal(string Table, int Rows, bool Commit, IReadOnlyList<Write> Writes);

public sealed class Postgres(string setting) : IResource, IDisposable
{
    static readonly ConcurrentDictionary<string, Lazy<Task<PostgresServer>>> servers = new();

    static readonly MethodInfo configureDbContext = typeof(EntityFrameworkServiceCollectionExtensions).GetMethods()
        .Single(m => m.Name == nameof(EntityFrameworkServiceCollectionExtensions.ConfigureDbContext)
            && m.GetParameters()[1].ParameterType == typeof(Action<DbContextOptionsBuilder>));

    readonly Fault fault = new();
    readonly List<Type> contexts = [];
    readonly List<Refusal> pending = [];
    PostgresServer? server;
    Gate? gate;
    Exception? failing;
    bool refusing;
    bool armed;

    public string Setting { get; } = setting;

    public string? Address { get; init; }

    public string Image { get; init; } = "postgres:17-alpine";

    public IReadOnlyList<string> Keep { get; init; } = [];

    PostgresServer Server => server ?? throw new InvalidOperationException("postgres is not provisioned: the service has not booted");

    public void FailWith(Exception? error)
    {
        failing = error ?? new NpgsqlException("the database refuses connections", new SocketException((int)SocketError.ConnectionRefused));
        refusing = error is null;
        if (armed)
        {
            Fail();
        }
    }

    public Task Refuse(string table, params Write[] writes) => Refuse(table, 0, false, writes);

    public Task RefuseAfter(string table, int rows, params Write[] writes) => Refuse(table, rows, false, writes);

    public Task RefuseCommit(string table, params Write[] writes) => Refuse(table, 0, true, writes);

    public async Task Heal()
    {
        failing = fault.Error = null;
        if (refusing)
        {
            refusing = false;
            gate?.Open();
            // What the gate dropped is still in the pools, and would fail its first command.
            NpgsqlConnection.ClearAllPools();
        }
        pending.Clear();
        if (server is not null)
        {
            await server.Lift(CancellationToken.None);
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

    public Task Reset(CancellationToken cancel) => Server.Reset(cancel);

    // What a step set before the boot waits for the start: the migration, the seed and the start go through.
    public async Task Arm(CancellationToken cancel)
    {
        armed = true;
        foreach (var refusal in pending)
        {
            await Server.Refuse(refusal, cancel);
        }
        pending.Clear();
        if (failing is not null)
        {
            Fail();
        }
    }

    public void Dispose() => gate?.Dispose();

    void Fail()
    {
        fault.Error = failing;
        if (refusing)
        {
            gate?.Close();
        }
    }

    async Task Refuse(string table, int rows, bool commit, Write[] writes)
    {
        var refusal = new Refusal(table, rows, commit, Written(writes));
        if (server is null)
        {
            pending.Add(refusal);
            return;
        }
        await server.Refuse(refusal, CancellationToken.None);
    }

    static IReadOnlyList<Write> Written(Write[] writes)
    {
        foreach (var write in writes.Where(write => !Enum.IsDefined(write)))
        {
            throw new ArgumentOutOfRangeException(nameof(writes), write, "refuse an insert, update or delete");
        }
        return [.. Enum.GetValues<Write>().Where(write => writes.Length == 0 || writes.Contains(write))];
    }

    // A context the container builds itself is the scope's to dispose, so the caller gets one of its own.
    static DbContext Context(IServiceProvider services, Type type)
    {
        var factory = services.GetService(typeof(IDbContextFactory<>).MakeGenericType(type));
        var made = factory?.GetType().GetMethod(nameof(IDbContextFactory<>.CreateDbContext), Type.EmptyTypes)?.Invoke(factory, null);
        return (DbContext)(made ?? ActivatorUtilities.CreateInstance(services, type));
    }

    static string Named(string service) => string.Concat(service.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));
}
