using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Specrig;

// Catches the host Program builds, as WebApplicationFactory and `dotnet ef` do: a builder
// the hosting library hands over before Build returns still takes services.
sealed class HostRun : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    // Another run in the same process builds its own host on its own thread.
    static readonly AsyncLocal<HostRun?> starting = new();

    readonly Action<IServiceCollection> configure;
    readonly TaskCompletionSource<IHost> built = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly List<IDisposable> subscriptions = [];

    HostRun(Action<IServiceCollection> configure) => this.configure = configure;

    public Task<int> Exited => exited.Task;

    public static HostRun Start(Assembly program, string[] args, Action<IServiceCollection> configure)
    {
        var entry = program.EntryPoint ?? throw new InvalidOperationException($"{program.GetName().Name} has no entry point");
        var run = new HostRun(configure);
        lock (run.subscriptions)
        {
            run.subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(run));
        }
        var thread = new Thread(() => run.Main(entry, args)) { IsBackground = true, Name = program.GetName().Name };
        starting.Value = run;
        thread.Start();
        starting.Value = null;
        return run;
    }

    public async Task<IHost> Started()
    {
        var host = await built.Task;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() => started.TrySetResult());
        var first = await Task.WhenAny(started.Task, exited.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (first == exited.Task)
        {
            await exited.Task;
            throw new InvalidOperationException("Program returned before its host started");
        }
        if (first != started.Task)
        {
            throw new TimeoutException("the host did not start within 30 s");
        }
        return host;
    }

    public void Stop()
    {
        if (built.Task.IsCompletedSuccessfully)
        {
            built.Task.Result.Services.GetService<IHostApplicationLifetime>()?.StopApplication();
        }
        exited.Task.ContinueWith(_ => { }, TaskScheduler.Default).Wait(TimeSpan.FromSeconds(10));
    }

    void Main(MethodInfo entry, string[] args)
    {
        try
        {
            var result = entry.Invoke(null, BindingFlags.DoNotWrapExceptions, null, entry.GetParameters().Length == 0 ? [] : [args], null);
            exited.TrySetResult(result switch
            {
                int code => code,
                Task<int> task => task.GetAwaiter().GetResult(),
                Task task => Wait(task),
                _ => 0,
            });
        }
        catch (Exception e)
        {
            built.TrySetException(e);
            exited.TrySetException(e);
        }
        finally
        {
            built.TrySetException(new InvalidOperationException("Program returned without building a host"));
        }
    }

    static int Wait(Task task)
    {
        task.GetAwaiter().GetResult();
        return 0;
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "Microsoft.Extensions.Hosting")
        {
            lock (subscriptions)
            {
                subscriptions.Add(listener.Subscribe(this));
            }
        }
    }

    public void OnNext(KeyValuePair<string, object?> e)
    {
        if (starting.Value != this)
        {
            return;
        }
        if (e.Key == "HostBuilding" && e.Value is IHostBuilder builder)
        {
            builder.ConfigureServices((_, services) => configure(services));
        }
        else if (e.Key == "HostBuilt" && e.Value is IHost host)
        {
            built.TrySetResult(host);
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void Dispose()
    {
        lock (subscriptions)
        {
            subscriptions.ForEach(s => s.Dispose());
            subscriptions.Clear();
        }
    }
}
