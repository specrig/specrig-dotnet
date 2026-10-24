using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

namespace Specrig;

public sealed class Settings : Dictionary<string, string?>;

public sealed class Grants : List<Type>;

public sealed class Workers : List<Type>;

public abstract class Harness<TProgram> : Rig
{
    protected sealed override Assembly Program => typeof(TProgram).Assembly;
}

public abstract class Rig : IDisposable
{
    static readonly Lazy<string> tests = new(FindTests);
    static readonly List<Func<Task>> ending = [];

    readonly Dictionary<Type, object> replaced = [];
    readonly Dictionary<IResource, string> addresses = [];
    readonly IReadOnlyList<IStandIn> neighbours;
    readonly IReadOnlyList<IResource> resources;
    readonly StandIns standIns;
    Booted? booted;

    // Not every runner ends a run where a hook can see it; a process exit comes to them all.
    static Rig() => AppDomain.CurrentDomain.ProcessExit += (_, _) => EndRun().ContinueWith(_ => { }, TaskScheduler.Default).Wait();

    protected Rig()
    {
        neighbours = [.. Properties<IStandIn>().Select(p => p.Value)];
        foreach (var (name, neighbour) in Properties<IStandIn>())
        {
            neighbour.Attach(name, Trace, () => Root);
        }
        standIns = new StandIns(neighbours);
        Bypass.Watch(standIns);
        resources = [.. Properties<IResource>().Select(p => p.Value)];
        foreach (var resource in resources)
        {
            resource.Attach(Trace, () => Boot());
        }
    }

    public static void Ending(Func<Task> down)
    {
        lock (ending)
        {
            ending.Add(down);
        }
    }

    public static async Task EndRun()
    {
        Func<Task>[] downs;
        lock (ending)
        {
            downs = [.. Enumerable.Reverse(ending)];
            ending.Clear();
        }
        var failures = new List<Exception>();
        foreach (var down in downs)
        {
            try
            {
                await down();
            }
            catch (Exception e)
            {
                failures.Add(e);
            }
        }
        if (failures.Count > 0)
        {
            throw new AggregateException("the run's resources did not all come down", failures);
        }
    }

    public abstract string Service { get; }

    protected virtual Settings Constants => new();

    protected virtual Grants Allow => [];

    protected virtual Workers Running => [];

    protected abstract Assembly Program { get; }

    public Trace Trace { get; } = new();

    public Clock Clock { get; } = new();

    public static string Tests => tests.Value;

    public static string Root => Path.GetDirectoryName(Tests)!;

    public HttpClient Http => Boot().Http ?? throw new InvalidOperationException($"{Service} has no server: its host registers no IServer");

    public FakeLogCollector Logs => Boot().Host.Services.GetFakeLogCollector();

    public AsyncServiceScope Scope() => Boot().Host.Services.CreateAsyncScope();

    public void Replace<T>(T instance) where T : class
    {
        if (booted is not null)
        {
            throw new InvalidOperationException($"{Service} has booted: replace {typeof(T).Name} in a Given, before a step reaches the service");
        }
        if (!Allow.Contains(typeof(T)))
        {
            throw new InvalidOperationException($"{typeof(T).Name} is not granted: Allow in {GetType().Name} does not name it");
        }
        replaced[typeof(T)] = instance;
    }

    public async Task Run<TJob>(CancellationToken cancel = default) where TJob : notnull
    {
        var host = Boot().Host;
        var call = Trace.Begin();
        string? error = null;
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            Bypass.Current.Value = standIns;
            var job = ActivatorUtilities.GetServiceOrCreateInstance<TJob>(scope.ServiceProvider);
            await (Runners.For(job) is { } runner ? runner.Run(job, scope.ServiceProvider, cancel) : Invoke(job, cancel));
        }
        catch (Exception e)
        {
            error = e.GetType().Name;
            throw;
        }
        finally
        {
            Trace.End(call, "in", "command", typeof(TJob).Name, error: error);
        }
    }

    // The command's host shares the service's resources, and the service's host is what readies them.
    public async Task<int> Command(string verb, params string[] args)
    {
        Boot();
        var call = Trace.Begin();
        string? error = null;
        try
        {
            using var run = Started([verb, .. args, .. Arguments()]);
            var code = await run.Exited;
            error = code == 0 ? null : $"exit {code}";
            return code;
        }
        catch (Exception e)
        {
            error = e.GetType().Name;
            throw;
        }
        finally
        {
            var request = args.Length == 0 ? null : new JsonArray([.. args.Select(a => JsonValue.Create(a))]);
            Trace.End(call, "in", "command", verb, request, error);
        }
    }

    public Task Settle() => Trace.Settle();

    public IReadOnlyList<string> Failures()
    {
        var failures = new List<string>();
        if (standIns.Outside is { Count: > 0 } outside)
        {
            failures.Add($"requests to an origin {GetType().Name} does not name: {string.Join(", ", outside)}");
        }
        failures.AddRange(neighbours.SelectMany(neighbour => neighbour.Failures()));
        failures.AddRange(resources.SelectMany(resource => resource.Failures()));
        return failures;
    }

    public void Dispose()
    {
        if (booted is { } done)
        {
            booted = null;
            done.Http?.Dispose();
            done.Run.Stop();
            done.Run.Dispose();
        }
        foreach (var resource in resources.OfType<IDisposable>())
        {
            resource.Dispose();
        }
        Bypass.Forget(standIns);
        GC.SuppressFinalize(this);
    }

    sealed record Booted(HostRun Run, IHost Host, HttpClient? Http);

    Booted Boot() => booted ??= Start();

    // The host's thread carries the case's mark, and so does everything the host starts from it.
    HostRun Started(string[] args)
    {
        var before = Bypass.Current.Value;
        Bypass.Current.Value = standIns;
        try
        {
            return HostRun.Start(Program, args, Configure);
        }
        finally
        {
            Bypass.Current.Value = before;
        }
    }

    Booted Start()
    {
        foreach (var resource in resources)
        {
            addresses[resource] = resource.Provision(Service, CancellationToken.None).GetAwaiter().GetResult();
        }
        var run = Started([.. Arguments()]);
        try
        {
            var host = run.Started().GetAwaiter().GetResult();
            Answered(host.Services.GetRequiredService<IConfiguration>());
            Ready(host.Services).GetAwaiter().GetResult();
            var server = host.Services.GetService<IServer>() as TestServer;
            return new Booted(run, host, server is null ? null : new HttpClient(server.CreateHandler()) { BaseAddress = server.BaseAddress });
        }
        catch
        {
            run.Stop();
            run.Dispose();
            throw;
        }
    }

    async Task Ready(IServiceProvider services)
    {
        foreach (var resource in resources)
        {
            await resource.Migrate(services, CancellationToken.None);
            await resource.Reset(CancellationToken.None);
        }
    }

    // On the command line, so the answers win over appsettings and the environment
    // even for what Program reads before it builds the host.
    IEnumerable<string> Arguments()
    {
        yield return "--environment=Production";
        yield return $"--applicationName={Program.GetName().Name}";
        yield return $"--contentRoot={AppContext.BaseDirectory}";
        foreach (var (key, value) in Answers())
        {
            yield return $"--{key}={value}";
        }
    }

    IEnumerable<KeyValuePair<string, string>> Answers() =>
        neighbours.Where(n => n.Setting is not null).Select(n => KeyValuePair.Create(n.Setting!, n.Origin))
            .Concat(addresses.Select(a => KeyValuePair.Create(a.Key.Setting, a.Value)))
            .Concat(Constants.Select(c => KeyValuePair.Create(c.Key, c.Value ?? "")));

    // Neither value is named: a resource's address may carry a password.
    void Answered(IConfiguration configuration)
    {
        foreach (var (key, value) in Answers())
        {
            if (configuration[key] != value)
            {
                throw new InvalidOperationException(
                    $"{key} reads other than {GetType().Name} answers: Program does not hand its args to the builder");
            }
        }
        if (Checks() is not { } checks)
        {
            return;
        }
        var answered = Answers().Select(a => a.Key).ToList();
        var shipped = (configuration as IConfigurationRoot)?.Providers.OfType<JsonConfigurationProvider>().ToList() ?? [];
        var wrong = new List<string>();
        if (checks.Settings.Where(key => !answered.Contains(key, StringComparer.OrdinalIgnoreCase) && !shipped.Any(p => p.TryGet(key, out _))).ToList()
            is { Count: > 0 } unanswered)
        {
            wrong.Add($"{Service} reads {string.Join(", ", unanswered)}, which neither appsettings.json nor {GetType().Name} answers: answer it in Constants");
        }
        if (checks.Unread(answered).ToList() is { Count: > 0 } unread)
        {
            wrong.Add($"{GetType().Name} answers {string.Join(", ", unread)}, which {Service} does not read");
        }
        if (checks.Consumers.Where(consumer => !Running.Any(type => type.FullName == consumer)).ToList() is { Count: > 0 } idle)
        {
            wrong.Add($"{string.Join(", ", idle)} binds a queue and Running does not name it: its events would reach nothing");
        }
        if (wrong.Count > 0)
        {
            throw new InvalidOperationException(string.Join("\n", wrong));
        }
    }

    static Checks? Checks()
    {
        try
        {
            return Specrig.Checks.From(Path.Combine(Tests, "checks.yaml"));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    void Configure(IServiceCollection services)
    {
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new StandInFilter(standIns));
        if (services.Any(d => d.ServiceType == typeof(IServer)))
        {
            services.RemoveAll<IServer>();
            services.AddSingleton<IServer>(provider => new TestServer(provider, provider.GetRequiredService<IOptions<TestServerOptions>>()));
            services.AddSingleton<IStartupFilter>(new Inbound(Trace, standIns));
        }
        KeepRunning(services);
        foreach (var resource in resources)
        {
            resource.Configure(services);
        }
        services.AddFakeLogging();
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(Clock);
        foreach (var (type, instance) in replaced)
        {
            services.RemoveAll(type);
            services.AddSingleton(type, instance);
        }
    }

    void KeepRunning(IServiceCollection services)
    {
        var running = Running.ToHashSet();
        var registered = new HashSet<Type>();
        foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService) && !d.IsKeyedService).ToList())
        {
            var type = hosted.ImplementationType ?? hosted.ImplementationInstance?.GetType() ?? hosted.ImplementationFactory?.Method.ReturnType;
            var owner = hosted.ImplementationFactory?.Method.DeclaringType ?? type;
            if (type is not null)
            {
                registered.Add(type);
            }
            if (owner is not null && Framework(owner) || type is not null && running.Contains(type))
            {
                continue;
            }
            services.Remove(hosted);
        }
        if (running.Except(registered).ToList() is { Count: > 0 } missing)
        {
            throw new InvalidOperationException($"Running names {string.Join(", ", missing.Select(t => t.Name))}, which {Service} does not run");
        }
    }

    static bool Framework(Type type) => type.Assembly.GetName().Name is { } name && (name.StartsWith("Microsoft.") || name.StartsWith("System."));

    IEnumerable<(string Name, T Value)> Properties<T>() =>
        GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(T).IsAssignableFrom(p.PropertyType))
            .Select(p => (p.Name, (T)p.GetValue(this)!));

    static Task Invoke(object job, CancellationToken cancel)
    {
        foreach (var name in (string[])["RunAsync", "ExecuteAsync", "Run", "Execute"])
        {
            var method = job.GetType().GetMethod(name, [typeof(CancellationToken)]) ?? job.GetType().GetMethod(name, Type.EmptyTypes);
            if (method is null)
            {
                continue;
            }
            var result = method.Invoke(job, BindingFlags.DoNotWrapExceptions, null, method.GetParameters().Length == 0 ? [] : [cancel], null);
            return result switch
            {
                Task task => task,
                ValueTask task => task.AsTask(),
                _ => Task.CompletedTask,
            };
        }
        throw new InvalidOperationException($"{job.GetType().Name} has no RunAsync, ExecuteAsync, Run or Execute to run it by");
    }

    static string FindTests()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("*.csproj").Any() && Directory.Exists(Path.Combine(dir.FullName, "feature")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException($"no directory above {AppContext.BaseDirectory} holds the suite's project beside a feature/ directory");
    }

    sealed class StandInFilter(StandIns standIns) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.PrimaryHandler = standIns.Handler();
        };
    }
}

public static class Scopes
{
    public static T Get<T>(this AsyncServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();
}
