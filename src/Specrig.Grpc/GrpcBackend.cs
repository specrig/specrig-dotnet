using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Grpc.Net.Client;

namespace Specrig;

public static class GrpcClients
{
    public static TClient Grpc<TClient>(this Rig harness) where TClient : ClientBase<TClient> =>
        (TClient)Activator.CreateInstance(typeof(TClient), GrpcChannel.ForAddress(harness.Http.BaseAddress!, new GrpcChannelOptions { HttpClient = harness.Http }))!;
}

public sealed class GrpcBackend(string origin, params ServiceDescriptor[] services) : IStandIn
{
    sealed record Declared(IMessage? Answer, StatusCode Code, string? Reason);

    readonly Lock gate = new();
    readonly Dictionary<MethodDescriptor, Declared> replies = [];
    readonly List<(MethodDescriptor Method, IMessage Request)> received = [];
    readonly List<string> unanswered = [];
    readonly List<string> violations = [];
    Trace trace = new();

    public string Origin { get; } = new Uri(origin).GetLeftPart(UriPartial.Authority);

    public string? Setting { get; init; }

    public string Name { get; private set; } = "";

    public void Reply(string method, IMessage answer)
    {
        var found = Method(method);
        lock (gate)
        {
            replies[found] = new Declared(answer, StatusCode.OK, null);
        }
    }

    public void Fail(string method, StatusCode code, string? reason = null)
    {
        var found = Method(method);
        lock (gate)
        {
            replies[found] = new Declared(null, code, reason);
        }
    }

    public IReadOnlyList<T> Requests<T>(string method) where T : IMessage
    {
        var found = Method(method);
        lock (gate)
        {
            return [.. received.Where(r => r.Method == found).Select(r => (T)r.Request)];
        }
    }

    MethodDescriptor Method(string name)
    {
        var methods = services.SelectMany(s => s.Methods).ToList();
        var found = methods.Where(m => m.FullName == name).ToList() is { Count: > 0 } full ? full
            : methods.Where(m => $"{m.Service.Name}.{m.Name}" == name).ToList() is { Count: > 0 } qualified ? qualified
            : methods.Where(m => m.Name == name).ToList();
        return found switch
        {
            [var one] => one,
            [] => throw new ArgumentException($"{Name} serves no {name}: {string.Join(", ", services.Select(s => s.FullName))} serve {string.Join(", ", methods.Select(m => $"{m.Service.Name}.{m.Name}"))}"),
            _ => throw new ArgumentException($"{name} is one of {string.Join(", ", found.Select(m => m.FullName))}: name the service"),
        };
    }

    void IStandIn.Attach(string name, Trace trace, Func<string> root) => (Name, this.trace) = (name, trace);

    IEnumerable<string> IStandIn.Failures()
    {
        var failures = new List<string>();
        lock (gate)
        {
            if (unanswered.Count > 0)
            {
                failures.Add($"{Name} was called where no step declared its answer: {string.Join(", ", unanswered)}");
            }
            if (violations.Count > 0)
            {
                failures.Add($"what {Name}'s services, {string.Join(", ", services.Select(s => s.FullName))}, do not allow: {string.Join("; ", violations)}");
            }
        }
        return failures;
    }

    public async Task<HttpResponseMessage> Answer(HttpRequestMessage request, CancellationToken cancel)
    {
        var path = request.RequestUri!.AbsolutePath;
        var call = trace.Begin();
        IMessage? message = null;
        try
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var method = parts is [var serviceName, var methodName] ? services.FirstOrDefault(s => s.FullName == serviceName)?.FindMethodByName(methodName) : null;
            if (method is null)
            {
                lock (gate)
                {
                    violations.Add($"{path}: {Name} serves no such method");
                }
                return Status(request, StatusCode.Unimplemented, null);
            }
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancel);
            message = method.InputType.Parser.ParseFrom(body.Length < 5 ? [] : body[5..]);
            Declared? reply;
            lock (gate)
            {
                received.Add((method, message));
                reply = replies.GetValueOrDefault(method);
                if (reply is null)
                {
                    unanswered.Add(path);
                }
                else if (reply.Answer is not null && reply.Answer.Descriptor != method.OutputType)
                {
                    violations.Add($"{path} answered a {reply.Answer.Descriptor.Name} where {method.Name} answers a {method.OutputType.Name}");
                }
            }
            return reply switch
            {
                null => Status(request, StatusCode.Unimplemented, "unanswered"),
                { Answer: { } answer } => Message(request, answer),
                _ => Status(request, reply.Code, reply.Reason),
            };
        }
        finally
        {
            trace.End(call, "out", "grpc", path, message is null ? null : JsonNode.Parse(JsonFormatter.Default.Format(message)));
        }
    }

    static HttpResponseMessage Message(HttpRequestMessage request, IMessage answer)
    {
        var payload = answer.ToByteArray();
        var frame = new byte[5 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), payload.Length);
        payload.CopyTo(frame, 5);
        var response = Response(request, frame);
        response.TrailingHeaders.Add("grpc-status", "0");
        return response;
    }

    static HttpResponseMessage Status(HttpRequestMessage request, StatusCode code, string? reason)
    {
        var response = Response(request, []);
        response.TrailingHeaders.Add("grpc-status", ((int)code).ToString());
        response.TrailingHeaders.Add("grpc-message", reason ?? code.ToString());
        if (reason is not null)
        {
            var status = new Google.Rpc.Status { Code = (int)code, Message = reason, Details = { Any.Pack(new ErrorInfo { Reason = reason, Domain = "specrig" }) } };
            response.TrailingHeaders.Add("grpc-status-details-bin", Convert.ToBase64String(status.ToByteArray()));
        }
        return response;
    }

    static HttpResponseMessage Response(HttpRequestMessage request, byte[] body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Version = HttpVersion.Version20, Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        return response;
    }
}
