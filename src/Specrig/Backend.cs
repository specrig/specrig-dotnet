using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Primitives;

namespace Specrig;

public sealed class Query : Dictionary<string, StringValues>;

public sealed record Received(string Method, string Path, IReadOnlyDictionary<string, StringValues> Query, JsonNode? Json)
{
    public T Body<T>() => Json.Deserialize<T>(Backend.Web)
        ?? throw new InvalidOperationException($"{Method} {Path} carried no body");
}

public sealed class Backend : IStandIn
{
    internal static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    sealed record Declared(Query Query, Func<Received, (int Status, JsonNode? Body)> Answer, IReadOnlyDictionary<string, string>? Headers);

    sealed record Waiting(string Request, TaskCompletionSource Released, CancellationToken Cancel);

    sealed record Holding(Query Query, List<Waiting> Waiting);

    readonly Lock gate = new();
    readonly OrderedDictionary<string, List<Declared>> replies = [];
    readonly OrderedDictionary<string, List<Holding>> holds = [];
    readonly OrderedDictionary<string, List<Query>> timeouts = [];
    readonly List<Received> received = [];
    readonly List<string> unanswered = [];
    readonly List<string> violations = [];
    Trace trace = new();
    Contract? contract;

    public Backend(string origin) => Origin = new Uri(origin).GetLeftPart(UriPartial.Authority);

    public string Origin { get; }
    public string? Setting { get; init; }
    public string? Contract { get; init; }
    public string? Failure { get; init; }
    public string Name { get; private set; } = "";

    void IStandIn.Attach(string name, Trace trace, Func<string> root) => Attach(name, trace, Contract switch
    {
        null => null,
        var file when Path.IsPathRooted(file) => Specrig.Contract.Load(file, name, Failure),
        var file => Specrig.Contract.Load(Path.Combine(root(), file), name, Failure),
    });

    internal void Attach(string name, Trace trace, Contract? contract)
    {
        Name = name;
        this.trace = trace;
        this.contract = contract;
    }

    IEnumerable<string> IStandIn.Failures()
    {
        if (Unanswered is { Count: > 0 } unanswered)
        {
            yield return $"{Name} was called where no step declared its answer: {string.Join(", ", unanswered)}";
        }
        if (Unreleased is { Count: > 0 } unreleased)
        {
            yield return $"{Name} holds what no step released: {string.Join(", ", unreleased)}";
        }
        if (Violations is { Count: > 0 } violations)
        {
            yield return $"what {Name}'s contract, {Contract}, does not allow: {string.Join("; ", violations)}";
        }
    }

    public void Reply(string method, string path, int status, object? body = null, IReadOnlyDictionary<string, string>? headers = null) =>
        ReplyTo(method, path, new Query(), status, body, headers);

    public void ReplyTo(string method, string path, Query query, int status, object? body = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        var json = Encode(body);
        Declare(method, path, new Declared(query, _ => (status, json), headers));
    }

    public void ReplyWith(string method, string path, Func<Received, (int Status, object? Body)> answer, IReadOnlyDictionary<string, string>? headers = null) =>
        Declare(method, path, new Declared(new Query(), received =>
        {
            var (status, body) = answer(received);
            return (status, Encode(body));
        }, headers));

    void Declare(string method, string path, Declared reply)
    {
        var key = $"{method} {path}";
        lock (gate)
        {
            replies[key] = [.. replies.GetValueOrDefault(key, []).Where(r => !Same(r.Query, reply.Query)), reply];
        }
    }

    static JsonNode? Encode(object? body) => body is null ? null : JsonSerializer.SerializeToNode(body, Web);

    public void TimeOut(string method, string path, Query? query = null)
    {
        var key = $"{method} {path}";
        query ??= new Query();
        lock (gate)
        {
            timeouts[key] = [.. timeouts.GetValueOrDefault(key, []).Where(q => !Same(q, query)), query];
        }
    }

    public void Heal()
    {
        lock (gate)
        {
            timeouts.Clear();
        }
    }

    public void Hold(string method, string path, Query? query = null)
    {
        var key = $"{method} {path}";
        query ??= new Query();
        lock (gate)
        {
            holds[key] = [.. holds.GetValueOrDefault(key, []).Where(h => !Same(h.Query, query)), new Holding(query, [])];
        }
    }

    public void Release(string method, string path, Query? query = null)
    {
        var key = $"{method} {path}";
        query ??= new Query();
        Holding hold;
        lock (gate)
        {
            var held = holds.GetValueOrDefault(key, []);
            hold = held.FirstOrDefault(h => Same(h.Query, query))
                ?? throw new InvalidOperationException($"no step held {key}{(query.Count > 0 ? " " + JsonSerializer.Serialize(query, Web) : "")}");
            holds[key] = [.. held.Where(h => h != hold)];
        }
        foreach (var waiting in hold.Waiting)
        {
            waiting.Released.TrySetResult();
        }
    }

    public IReadOnlyList<Received> Requests(string method, string path)
    {
        lock (gate)
        {
            return [.. received.Where(r => r.Method == method && Takes(path, r.Path))];
        }
    }

    public IReadOnlyList<string> Unanswered
    {
        get
        {
            lock (gate)
            {
                return [.. unanswered];
            }
        }
    }

    public IReadOnlyList<string> Violations
    {
        get
        {
            lock (gate)
            {
                return [.. violations];
            }
        }
    }

    // A request the service gave up on has nobody left to answer.
    public IReadOnlyList<string> Unreleased
    {
        get
        {
            lock (gate)
            {
                return [.. holds.Values.SelectMany(held => held).SelectMany(h => h.Waiting)
                    .Where(w => !w.Cancel.IsCancellationRequested && !w.Released.Task.IsCompleted)
                    .Select(w => w.Request)];
            }
        }
    }

    public async Task<HttpResponseMessage> Answer(HttpRequestMessage request, CancellationToken cancel)
    {
        var method = request.Method.Method;
        var url = request.RequestUri!;
        var key = $"{method} {url.AbsolutePath}";
        var query = Parse(url.Query);
        var body = request.Content is null ? null : Json(await request.Content.ReadAsStringAsync(cancel));
        var call = trace.Begin();
        try
        {
            Waiting? waiting = null;
            var timedOut = false;
            var got = new Received(method, url.AbsolutePath, query, body);
            lock (gate)
            {
                received.Add(got);
                violations.AddRange(contract?.Request(method, url.AbsolutePath, body) ?? []);
                if (Pick(Candidates(timeouts, method, url.AbsolutePath), query, q => q) is not null)
                {
                    timedOut = true;
                }
                else if (Pick(Candidates(holds, method, url.AbsolutePath), query, h => h.Query) is { } hold)
                {
                    waiting = new Waiting(key + url.Query, new(TaskCreationOptions.RunContinuationsAsynchronously), cancel);
                    hold.Waiting.Add(waiting);
                }
            }
            if (timedOut)
            {
                // The client's own timeout is not visible from under it, so the message carries the default.
                throw new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
                    new TimeoutException("The operation was canceled."));
            }
            if (waiting is not null)
            {
                trace.Held();
                try
                {
                    await waiting.Released.Task.WaitAsync(cancel);
                }
                finally
                {
                    trace.Released();
                }
            }
            return Fulfil(request, key, got);
        }
        finally
        {
            trace.End(call, "out", "http", contract?.Name(method, url.AbsolutePath) ?? key, body);
        }
    }

    HttpResponseMessage Fulfil(HttpRequestMessage request, string key, Received got)
    {
        Declared? reply;
        lock (gate)
        {
            reply = Pick(Candidates(replies, got.Method, got.Path), got.Query, r => r.Query);
            if (reply is null)
            {
                unanswered.Add(key + request.RequestUri!.Query);
            }
        }
        if (reply is null)
        {
            return Respond(request, 501, new JsonObject { ["code"] = "unanswered", ["message"] = key });
        }
        var (status, body) = reply.Answer(got);
        lock (gate)
        {
            violations.AddRange(contract?.Response(got.Method, got.Path, status, body ?? new JsonObject()) ?? []);
        }
        return Respond(request, status, status == 204 ? null : body ?? new JsonObject(), reply.Headers);
    }

    static HttpResponseMessage Respond(HttpRequestMessage request, int status, JsonNode? body, IReadOnlyDictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { RequestMessage = request };
        if (body is not null)
        {
            response.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        foreach (var (name, value) in headers ?? ReadOnlyDictionary<string, string>.Empty)
        {
            if (!response.Headers.TryAddWithoutValidation(name, value))
            {
                response.Content ??= new ByteArrayContent([]);
                response.Content.Headers.Remove(name);
                response.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
        return response;
    }

    static List<T> Candidates<T>(OrderedDictionary<string, List<T>> declarations, string method, string path)
    {
        if (declarations.TryGetValue($"{method} {path}", out var exact) && exact.Count > 0)
        {
            return exact;
        }
        var prefix = $"{method} ";
        return declarations.FirstOrDefault(d => d.Value.Count > 0 && d.Key.StartsWith(prefix) && Takes(d.Key[prefix.Length..], path)).Value ?? [];
    }

    static bool Takes(string template, string path)
    {
        var segments = template.Split('/');
        var wanted = path.Split('/');
        return segments.Length == wanted.Length && segments.Zip(wanted).All(s => s.First == s.Second || s.First is ['{', .., '}']);
    }

    static T? Pick<T>(IEnumerable<T> candidates, IReadOnlyDictionary<string, StringValues> query, Func<T, Query> of) where T : class
    {
        T? found = null;
        foreach (var candidate in candidates)
        {
            if (Matches(of(candidate), query) && of(candidate).Count >= (found is null ? 0 : of(found).Count))
            {
                found = candidate;
            }
        }
        return found;
    }

    static bool Matches(Query wanted, IReadOnlyDictionary<string, StringValues> got) =>
        wanted.All(w => got.TryGetValue(w.Key, out var values)
            && (w.Value.Count == 1 ? values.Count > 0 && values[0] == w.Value[0] : values.SequenceEqual(w.Value)));

    static bool Same(Query a, Query b) =>
        a.Count == b.Count && a.All(entry => b.TryGetValue(entry.Key, out var values) && values.SequenceEqual(entry.Value));

    internal static Dictionary<string, StringValues> Parse(string search)
    {
        var values = new Dictionary<string, List<string>>();
        foreach (var pair in search.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=');
            var name = Decode(at < 0 ? pair : pair[..at]);
            var value = at < 0 ? "" : Decode(pair[(at + 1)..]);
            (values.TryGetValue(name, out var list) ? list : values[name] = []).Add(value);
        }
        return values.ToDictionary(entry => entry.Key, entry => new StringValues([.. entry.Value]));
    }

    static string Decode(string part) => Uri.UnescapeDataString(part.Replace('+', ' '));

    internal static JsonNode? Json(string raw)
    {
        if (raw.Length == 0)
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return JsonValue.Create(raw);
        }
    }
}
