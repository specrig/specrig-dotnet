namespace Specrig;

public interface IStandIn
{
    string Origin { get; }

    string? Setting { get; }

    void Attach(string name, Trace trace, Func<string> root);

    Task<HttpResponseMessage> Answer(HttpRequestMessage request, CancellationToken cancel);

    IEnumerable<string> Failures();
}

public sealed class StandIns(IReadOnlyList<IStandIn> standIns)
{
    readonly Lock gate = new();
    readonly List<string> outside = [];

    public IReadOnlyList<string> Outside
    {
        get
        {
            lock (gate)
            {
                return [.. outside];
            }
        }
    }

    // A handler of its own for every client, so the client factory's rotation disposes none of the stand-ins.
    public HttpMessageHandler Handler() => new Relay(this);

    public void Went(HttpRequestMessage request)
    {
        lock (gate)
        {
            outside.Add($"{request.Method} {request.RequestUri}, past the stand-ins");
        }
    }

    Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancel)
    {
        var url = request.RequestUri ?? throw new InvalidOperationException("a request with no address");
        var origin = url.GetLeftPart(UriPartial.Authority);
        var standIn = standIns.FirstOrDefault(s => string.Equals(s.Origin, origin, StringComparison.OrdinalIgnoreCase));
        if (standIn is not null)
        {
            return standIn.Answer(request, cancel);
        }
        lock (gate)
        {
            outside.Add($"{request.Method} {url}");
        }
        return Task.FromException<HttpResponseMessage>(new HttpRequestException($"{origin} is no neighbour the harness names"));
    }

    sealed class Relay(StandIns standIns) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            standIns.Send(request, cancellationToken);
    }
}
