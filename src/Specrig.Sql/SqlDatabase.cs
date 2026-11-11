using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Specrig;

public enum Write
{
    Insert,
    Update,
    Delete,
}

sealed record Refusal(string Table, int Rows, bool Commit, IReadOnlyList<Write> Writes);

public abstract class SqlDatabase : IResource, IDisposable
{
    static readonly ConcurrentDictionary<string, Lazy<Task<Store>>> stores = new();

    readonly Fault fault = new();
    readonly List<Type> contexts = [];
    readonly List<Refusal> pending = [];
    Store? store;
    Gate? gate;
    Func<DbConnection, Exception>? failing;
    bool refusing;
    bool armed;

    internal SqlDatabase(string setting) => Setting = setting;

    public string Setting { get; }

    public IReadOnlyList<string> Keep { get; init; } = [];

    internal abstract string Kind { get; }

    Store Store => store ?? throw new InvalidOperationException($"{Kind} is not provisioned: the service has not booted");

    public void FailWith(Exception? error)
    {
        failing = error is null ? Refused : _ => error;
        refusing = error is null;
        if (armed)
        {
            Fail();
        }
    }

    public Task Refuse(string table, params Write[] writes) => Refuse(table, 0, false, writes);

    public Task RefuseAfter(string table, int rows, params Write[] writes) => Refuse(table, rows, false, writes);

    public async Task Heal()
    {
        failing = fault.Error = null;
        if (refusing)
        {
            refusing = false;
            gate?.Open();
            // What the gate dropped is still in the pools, and would fail its first command.
            store?.Forget();
        }
        pending.Clear();
        if (store is not null)
        {
            await store.Lift(CancellationToken.None);
        }
    }

    public Task<string> Provision(string service, CancellationToken cancel) =>
        Provisioned(string.Concat(service.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')));

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
            var options = typeof(DbContextOptions<>).MakeGenericType(type);
            for (var i = 0; i < services.Count; i++)
            {
                if (services[i].ServiceType == options)
                {
                    services[i] = Intercepted(services[i], interceptors);
                }
            }
        }
    }

    public Task Migrate(IServiceProvider services, CancellationToken cancel) => Store.Once(async () =>
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
        await Store.Discover(Keep, cancel);
    });

    public Task Reset(CancellationToken cancel) => Store.Reset(cancel);

    // What a step set before the boot waits for the start: the migration, the seed and the start go through.
    public async Task Arm(CancellationToken cancel)
    {
        armed = true;
        foreach (var refusal in pending)
        {
            await Store.Refuse(refusal, cancel);
        }
        pending.Clear();
        if (failing is not null)
        {
            Fail();
        }
    }

    public void Dispose() => gate?.Dispose();

    internal abstract Task<string> Provisioned(string name);

    internal abstract Exception Refused(DbConnection connection);

    internal async Task<T> Shared<T>(string key, Func<Task<T>> start) where T : Store
    {
        var shared = (T)await stores.GetOrAdd($"{Kind}\n{key}", _ => new(async () => await start())).Value;
        store = shared;
        return shared;
    }

    internal (string Host, int Port) Gated(string host, int port)
    {
        gate ??= new Gate(host, port);
        return ("127.0.0.1", gate.Port);
    }

    internal async Task Refuse(string table, int rows, bool commit, Write[] writes)
    {
        var refusal = new Refusal(table, rows, commit, Written(writes));
        if (store is null)
        {
            pending.Add(refusal);
            return;
        }
        await store.Refuse(refusal, CancellationToken.None);
    }

    void Fail()
    {
        if (refusing)
        {
            gate?.Close();
        }
        fault.Error = failing;
    }

    static IReadOnlyList<Write> Written(Write[] writes)
    {
        foreach (var write in writes.Where(write => !Enum.IsDefined(write)))
        {
            throw new ArgumentOutOfRangeException(nameof(writes), write, "refuse an insert, update or delete");
        }
        return [.. Enum.GetValues<Write>().Where(write => writes.Length == 0 || writes.Contains(write))];
    }

    static ServiceDescriptor Intercepted(ServiceDescriptor registered, IInterceptor[] interceptors) => new(registered.ServiceType, provider =>
    {
        var options = registered.ImplementationFactory?.Invoke(provider) ?? registered.ImplementationInstance
            ?? ActivatorUtilities.CreateInstance(provider, registered.ImplementationType!);
        return new DbContextOptionsBuilder((DbContextOptions)options).AddInterceptors(interceptors).Options;
    }, registered.Lifetime);

    // A context the container builds itself is the scope's to dispose, so the caller gets one of its own.
    static DbContext Context(IServiceProvider services, Type type)
    {
        var factory = services.GetService(typeof(IDbContextFactory<>).MakeGenericType(type));
        var made = factory?.GetType().GetMethod(nameof(IDbContextFactory<>.CreateDbContext), Type.EmptyTypes)?.Invoke(factory, null);
        return (DbContext)(made ?? ActivatorUtilities.CreateInstance(services, type));
    }
}
