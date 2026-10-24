using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Specrig.RabbitMQ.Tests;

public class RabbitBusTests
{
    readonly RabbitBus bus = new("ConnectionStrings:Rabbit");
    readonly IConnection connection;
    readonly List<string> received = [];

    public RabbitBusTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Proxy.Of<IConnection>((method, _) => throw new InvalidOperationException(method.Name)));
        ((IResource)bus).Configure(services);
        connection = services.BuildServiceProvider().GetRequiredService<IConnection>();
    }

    async Task<IChannel> Consume(string exchange, string type, string queue, string key, bool settle = true)
    {
        var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(exchange, type);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(queue, exchange, key);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            received.Add($"{queue} {delivery.RoutingKey}");
            if (settle)
            {
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
            }
        };
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer);
        return channel;
    }

    [Fact]
    public async Task A_topic_binding_takes_a_key_by_its_words()
    {
        await Consume("billing", ExchangeType.Topic, "one-word", "invoices.*");
        await Consume("billing", ExchangeType.Topic, "any-words", "invoices.#");

        Assert.Equal([Settled.Acked, Settled.Acked], await bus.Deliver("invoices.paid", new { order = 7 }));
        Assert.Equal([Settled.Acked], await bus.Deliver("invoices.paid.late", new { order = 7 }));
        Assert.Equal(["one-word invoices.paid", "any-words invoices.paid", "any-words invoices.paid.late"], received);
    }

    [Fact]
    public async Task A_key_bound_on_two_exchanges_is_delivered_through_the_one_named()
    {
        await Consume("billing", ExchangeType.Topic, "from-billing", "invoices.paid");
        await Consume("audit", ExchangeType.Direct, "from-audit", "invoices.paid");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => bus.Deliver("invoices.paid", new { }));
        Assert.Equal("invoices.paid is bound on billing and audit: name the exchange to deliver it through", refused.Message);

        await bus.Deliver("invoices.paid", new { }, exchange: "audit");
        Assert.Equal(["from-audit invoices.paid"], received);
    }

    [Fact]
    public async Task A_delivery_its_consumer_leaves_unsettled_is_a_failure_named()
    {
        await Consume("billing", ExchangeType.Topic, "lazy", "invoices.paid", settle: false);

        Assert.Equal([Settled.Unsettled], await bus.Deliver("invoices.paid", new { order = 7 }));
        Assert.Equal(["a delivery of invoices.paid its consumer left unsettled"], ((IResource)bus).Failures());
    }

    [Fact]
    public async Task What_the_service_publishes_is_recorded_until_the_bus_fails()
    {
        var channel = await connection.CreateChannelAsync();
        await channel.BasicPublishAsync("reminders", "reminders.sent", "{\"order\":7}"u8.ToArray());

        var sent = Assert.Single(bus.Published("reminders.sent"));
        Assert.Equal(("reminders", 7), (sent.Exchange, sent.Json!["order"]!.GetValue<int>()));

        bus.FailWith(null);
        await Assert.ThrowsAsync<AlreadyClosedException>(async () => await channel.BasicPublishAsync("reminders", "reminders.sent", "{}"u8.ToArray()));
    }

    [Fact]
    public async Task What_the_stand_in_does_not_do_is_refused_by_name()
    {
        var channel = await connection.CreateChannelAsync();

        var refused = await Assert.ThrowsAsync<NotSupportedException>(() => channel.BasicGetAsync("any", autoAck: true));
        Assert.StartsWith("IChannel.BasicGetAsync:", refused.Message);
    }
}
