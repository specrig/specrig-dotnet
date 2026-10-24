using System.Diagnostics;

namespace Specrig;

// The stand-ins take the place of the handler that reports to this listener, so every
// request it reports went around them, to the network.
sealed class Bypass : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    static readonly Bypass listening = new();
    static readonly HashSet<StandIns> alive = [];

    public static readonly AsyncLocal<StandIns?> Current = new();

    static Bypass() => DiagnosticListener.AllListeners.Subscribe(listening);

    public static void Watch(StandIns standIns)
    {
        lock (alive)
        {
            alive.Add(standIns);
        }
    }

    public static void Forget(StandIns standIns)
    {
        lock (alive)
        {
            alive.Remove(standIns);
        }
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "HttpHandlerDiagnosticListener")
        {
            listener.Subscribe(this);
        }
    }

    public void OnNext(KeyValuePair<string, object?> e)
    {
        if (e.Key != "System.Net.Http.HttpRequestOut.Start"
            || e.Value?.GetType().GetProperty("Request")?.GetValue(e.Value) is not HttpRequestMessage request)
        {
            return;
        }
        StandIns[] cases;
        lock (alive)
        {
            cases = Current.Value is { } current ? [current] : [.. alive];
        }
        foreach (var standIns in cases)
        {
            standIns.Went(request);
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }
}
