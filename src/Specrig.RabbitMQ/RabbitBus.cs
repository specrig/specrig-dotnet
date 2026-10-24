using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Specrig;

public enum Settled
{
    Acked,
    Requeued,
    Dropped,
    Unsettled,
}

public sealed record Message(string Exchange, string RoutingKey, IReadOnlyDictionary<string, object?> Headers, ReadOnlyMemory<byte> Bytes)
{
    public JsonNode? Json => Backend.Json(Encoding.UTF8.GetString(Bytes.Span));

    public T Body<T>() => JsonSerializer.Deserialize<T>(Bytes.Span, Backend.Web)
        ?? throw new InvalidOperationException($"{RoutingKey} carried no body");
}

public sealed class RabbitBus(string setting) : IResource
{
    sealed record Binding(string Queue, string Exchange, string Key);

    sealed record Consumer(string Queue, string Tag, bool AutoAck, IAsyncBasicConsumer Handler);

    sealed record Delivery(string Key)
    {
        public Settled? Settled { get; set; }
    }

    readonly Lock gate = new();
    readonly Dictionary<string, string> exchanges = [];
    readonly HashSet<string> queues = [];
    readonly List<Binding> bindings = [];
    readonly List<Consumer> consumers = [];
    readonly List<Message> published = [];
    readonly Dictionary<ulong, Delivery> deliveries = [];
    Trace trace = new();
    Action boot = () => { };
    long tags;
    Exception? fault;

    public string Setting { get; } = setting;

    public string Address { get; init; } = "amqp://rabbit.harness.invalid/";

    public void FailWith(Exception? error) =>
        fault = error ?? new AlreadyClosedException(new ShutdownEventArgs(ShutdownInitiator.Library, 320, "CONNECTION_FORCED", (object?)null));

    public IReadOnlyList<Message> Published(string? routingKey = null)
    {
        lock (gate)
        {
            return [.. published.Where(m => routingKey is null || m.RoutingKey == routingKey)];
        }
    }

    public async Task<IReadOnlyList<Settled>> Deliver(string routingKey, object? body, string? exchange = null, IDictionary<string, object?>? headers = null)
    {
        var bytes = Bytes(body);
        boot();
        var targets = await Route(routingKey, exchange);
        var call = trace.Begin();
        var settled = new List<Settled>();
        try
        {
            foreach (var (consumer, from) in targets)
            {
                var tag = (ulong)Interlocked.Increment(ref tags);
                var delivery = new Delivery(routingKey) { Settled = consumer.AutoAck ? Specrig.Settled.Acked : null };
                lock (gate)
                {
                    deliveries[tag] = delivery;
                }
                var properties = new BasicProperties { Headers = headers, ContentType = "application/json" };
                await consumer.Handler.HandleBasicDeliverAsync(consumer.Tag, tag, false, from, routingKey, properties, bytes);
                lock (gate)
                {
                    settled.Add(delivery.Settled ?? Specrig.Settled.Unsettled);
                }
            }
            return settled;
        }
        finally
        {
            var refused = settled.FirstOrDefault(s => s != Specrig.Settled.Acked);
            trace.End(call, "in", "event", routingKey, Backend.Json(Encoding.UTF8.GetString(bytes.Span)),
                settled.Count == 0 || refused == Specrig.Settled.Acked ? null : refused.ToString().ToLowerInvariant());
        }
    }

    void IResource.Attach(Trace trace, Action boot) => (this.trace, this.boot) = (trace, boot);

    Task<string> IResource.Provision(string service, CancellationToken cancel) => Task.FromResult(Address);

    void IResource.Configure(IServiceCollection services)
    {
        foreach (var registered in services.Where(d => d.ServiceType == typeof(IConnectionFactory) || d.ServiceType == typeof(IConnection)).ToList())
        {
            object standIn = registered.ServiceType == typeof(IConnection) ? Connection() : Factory();
            services.Remove(registered);
            services.Add(registered.IsKeyedService
                ? ServiceDescriptor.KeyedSingleton(registered.ServiceType, registered.ServiceKey, standIn)
                : ServiceDescriptor.Singleton(registered.ServiceType, standIn));
        }
    }

    Task IResource.Migrate(IServiceProvider services, CancellationToken cancel) => Task.CompletedTask;

    Task IResource.Reset(CancellationToken cancel) => Task.CompletedTask;

    IEnumerable<string> IResource.Failures()
    {
        lock (gate)
        {
            return [.. deliveries.Values.Where(d => d.Settled is null).Select(d => $"a delivery of {d.Key} its consumer left unsettled")];
        }
    }

    // A consumer binds its queue as its hosted service starts, beside the host rather than before it.
    async Task<List<(Consumer Consumer, string Exchange)>> Route(string key, string? exchange)
    {
        var deadline = Environment.TickCount64 + 5000;
        for (; ; )
        {
            string? missing;
            lock (gate)
            {
                var matched = bindings.Where(b => (exchange is null || b.Exchange == exchange) && Takes(b, key)).ToList();
                if (exchange is null or "" && queues.Contains(key))
                {
                    matched.Add(new Binding(key, "", key));
                }
                if (exchange is null && matched.Select(b => b.Exchange).Distinct().ToList() is { Count: > 1 } several)
                {
                    throw new InvalidOperationException($"{key} is bound on {string.Join(" and ", several)}: name the exchange to deliver it through");
                }
                var targets = matched.Select(b => b.Queue).Distinct()
                    .Select(queue => (Queue: queue, Consumer: consumers.FirstOrDefault(c => c.Queue == queue), From: matched.First(b => b.Queue == queue).Exchange))
                    .ToList();
                if (targets.Count > 0 && targets.All(t => t.Consumer is not null))
                {
                    return [.. targets.Select(t => (t.Consumer!, t.From))];
                }
                missing = targets.Count == 0
                    ? $"no queue of the service binds {key}{(exchange is null ? "" : $" on {exchange}")}; it binds {(bindings.Count == 0 ? "nothing" : string.Join(", ", bindings.Select(b => $"{b.Exchange}:{b.Key} → {b.Queue}")))}"
                    : $"{string.Join(", ", targets.Where(t => t.Consumer is null).Select(t => t.Queue))} takes {key}, and nothing consumes from it: is its hosted service in Running?";
            }
            if (Environment.TickCount64 > deadline)
            {
                throw new InvalidOperationException(missing);
            }
            await Task.Delay(10);
        }
    }

    bool Takes(Binding binding, string key) => exchanges.GetValueOrDefault(binding.Exchange, ExchangeType.Topic) switch
    {
        ExchangeType.Fanout => true,
        ExchangeType.Direct => binding.Key == key,
        ExchangeType.Headers => throw new NotSupportedException($"{binding.Exchange} is a headers exchange, which the bus stand-in does not route"),
        _ => Topic(binding.Key.Split('.'), key.Split('.')),
    };

    static bool Topic(ReadOnlySpan<string> pattern, ReadOnlySpan<string> words)
    {
        if (pattern.IsEmpty)
        {
            return words.IsEmpty;
        }
        if (pattern[0] == "#")
        {
            for (var skip = 0; skip <= words.Length; skip++)
            {
                if (Topic(pattern[1..], words[skip..]))
                {
                    return true;
                }
            }
            return false;
        }
        return !words.IsEmpty && (pattern[0] == "*" || pattern[0] == words[0]) && Topic(pattern[1..], words[1..]);
    }

    static ReadOnlyMemory<byte> Bytes(object? body) => body switch
    {
        null => ReadOnlyMemory<byte>.Empty,
        byte[] bytes => bytes,
        ReadOnlyMemory<byte> memory => memory,
        string text => Encoding.UTF8.GetBytes(text),
        _ => JsonSerializer.SerializeToUtf8Bytes(body, Backend.Web),
    };

    IConnectionFactory Factory() => Proxy.Of<IConnectionFactory>((method, _) => method.Name switch
    {
        nameof(IConnectionFactory.CreateConnectionAsync) => Task.FromResult(Connection()),
        _ when method.IsSpecialName => Proxy.Done(method.ReturnType),
        _ => throw Unsupported(method),
    });

    IConnection Connection() => Proxy.Of<IConnection>((method, _) => method.Name switch
    {
        nameof(IConnection.CreateChannelAsync) => Task.FromResult(Channel()),
        "get_IsOpen" => true,
        nameof(IConnection.CloseAsync) or nameof(IConnection.UpdateSecretAsync) or "Dispose" or "DisposeAsync" => Proxy.Done(method.ReturnType),
        _ when method.IsSpecialName => Proxy.Done(method.ReturnType),
        _ => throw Unsupported(method),
    });

    IChannel Channel() => Proxy.Of<IChannel>((method, args) =>
        {
            switch (method.Name)
            {
                case nameof(IChannel.ExchangeDeclareAsync):
                    lock (gate)
                    {
                        exchanges[(string)args[0]!] = (string)args[1]!;
                    }
                    break;
                case nameof(IChannel.QueueDeclareAsync):
                    var queue = args[0] is "" ? $"amq.gen-{Guid.NewGuid():N}" : (string)args[0]!;
                    lock (gate)
                    {
                        queues.Add(queue);
                        return Task.FromResult(new QueueDeclareOk(queue, 0, (uint)consumers.Count(c => c.Queue == queue)));
                    }
                case nameof(IChannel.QueueDeclarePassiveAsync):
                    return Task.FromResult(new QueueDeclareOk((string)args[0]!, 0, 0));
                case nameof(IChannel.QueueBindAsync):
                    lock (gate)
                    {
                        bindings.Add(new Binding((string)args[0]!, (string)args[1]!, (string)args[2]!));
                    }
                    break;
                case nameof(IChannel.QueueUnbindAsync):
                    lock (gate)
                    {
                        bindings.Remove(new Binding((string)args[0]!, (string)args[1]!, (string)args[2]!));
                    }
                    break;
                case nameof(IChannel.BasicConsumeAsync):
                    return Consume((string)args[0]!, (bool)args[1]!, (string)args[2]!, (IAsyncBasicConsumer)args[6]!);
                case nameof(IChannel.BasicCancelAsync):
                    lock (gate)
                    {
                        consumers.RemoveAll(c => c.Tag == (string)args[0]!);
                    }
                    break;
                case nameof(IChannel.BasicPublishAsync):
                    Publish(Name(args[0]), Name(args[1]), (IReadOnlyBasicProperties)args[3]!, (ReadOnlyMemory<byte>)args[4]!);
                    break;
                case nameof(IChannel.BasicAckAsync):
                    Settle((ulong)args[0]!, (bool)args[1]!, Specrig.Settled.Acked);
                    break;
                case nameof(IChannel.BasicNackAsync):
                    Settle((ulong)args[0]!, (bool)args[1]!, (bool)args[2]! ? Specrig.Settled.Requeued : Specrig.Settled.Dropped);
                    break;
                case nameof(IChannel.BasicRejectAsync):
                    Settle((ulong)args[0]!, false, (bool)args[1]! ? Specrig.Settled.Requeued : Specrig.Settled.Dropped);
                    break;
                case "get_IsOpen":
                    return true;
                case "get_ChannelNumber":
                    return 1;
                case "get_ContinuationTimeout":
                    return TimeSpan.FromSeconds(20);
                case nameof(IChannel.ExchangeDeclarePassiveAsync) or nameof(IChannel.ExchangeDeleteAsync) or nameof(IChannel.QueueDeleteAsync)
                    or nameof(IChannel.QueuePurgeAsync) or nameof(IChannel.BasicQosAsync) or nameof(IChannel.GetNextPublishSequenceNumberAsync)
                    or nameof(IChannel.MessageCountAsync) or nameof(IChannel.ConsumerCountAsync) or nameof(IChannel.CloseAsync)
                    or "Dispose" or "DisposeAsync":
                    break;
                default:
                    if (!method.IsSpecialName)
                    {
                        throw Unsupported(method);
                    }
                    break;
            }
            return Proxy.Done(method.ReturnType);
        });

    async Task<string> Consume(string queue, bool autoAck, string tag, IAsyncBasicConsumer handler)
    {
        tag = tag is "" ? $"amq.ctag-{Guid.NewGuid():N}" : tag;
        lock (gate)
        {
            consumers.Add(new Consumer(queue, tag, autoAck, handler));
        }
        await handler.HandleBasicConsumeOkAsync(tag);
        return tag;
    }

    void Publish(string exchange, string key, IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body)
    {
        if (fault is not null)
        {
            throw fault;
        }
        var message = new Message(exchange, key, properties.Headers?.ToDictionary() ?? [], body.ToArray());
        var call = trace.Begin();
        lock (gate)
        {
            published.Add(message);
        }
        trace.End(call, "out", "event", key, message.Json);
    }

    void Settle(ulong tag, bool multiple, Settled how)
    {
        lock (gate)
        {
            foreach (var (_, delivery) in deliveries.Where(d => d.Key == tag || multiple && d.Key < tag))
            {
                delivery.Settled ??= how;
            }
        }
    }

    static string Name(object? name) => name switch
    {
        string text => text,
        CachedString cached => cached.Value,
        _ => throw new ArgumentException($"no exchange or routing key in a {name?.GetType().Name}"),
    };

    static NotSupportedException Unsupported(MethodInfo method) =>
        new($"{method.DeclaringType?.Name}.{method.Name}: the bus stand-in declares, binds, publishes, consumes and settles, and does nothing else");
}
