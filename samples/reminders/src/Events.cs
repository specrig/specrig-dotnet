using System.Collections.Concurrent;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Reminders;

public sealed record Paid(int Order);

public sealed class PaidOrders
{
    readonly ConcurrentDictionary<int, bool> orders = new();

    public bool Has(int order) => orders.ContainsKey(order);

    public void Add(int order) => orders[order] = true;
}

public sealed class Announcer(IConnection connection)
{
    public async Task Sent(Reminder reminder, CancellationToken cancel)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancel);
        await channel.ExchangeDeclareAsync("reminders", ExchangeType.Topic, durable: true, cancellationToken: cancel);
        await channel.BasicPublishAsync("reminders", "reminders.sent", JsonSerializer.SerializeToUtf8Bytes(reminder, JsonSerializerOptions.Web), cancel);
    }
}

// Billing may list an invoice as unpaid a while after it says the invoice is paid.
public sealed class InvoicesPaid(IConnection connection, PaidOrders paid) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.ExchangeDeclareAsync("billing", ExchangeType.Topic, durable: true, cancellationToken: stoppingToken);
        var queue = await channel.QueueDeclareAsync("reminders.invoices-paid", durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
        await channel.QueueBindAsync(queue.QueueName, "billing", "invoices.paid", cancellationToken: stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            if (Read(delivery.Body) is not { Order: > 0 } invoice)
            {
                await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false);
                return;
            }
            paid.Add(invoice.Order);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
        };
        await channel.BasicConsumeAsync(queue.QueueName, autoAck: false, consumer, stoppingToken);
    }

    static Paid? Read(ReadOnlyMemory<byte> body)
    {
        try
        {
            return JsonSerializer.Deserialize<Paid>(body.Span, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
