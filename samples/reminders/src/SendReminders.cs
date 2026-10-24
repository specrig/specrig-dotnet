using Quartz;

namespace Reminders;

public sealed class SendReminders(Billing billing, PaidOrders paid, Announcer announcer, TimeProvider clock)
{
    public Task RunAsync(CancellationToken cancel) => RunAsync(int.MaxValue, cancel);

    public async Task RunAsync(int limit, CancellationToken cancel)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var due = (await billing.Unpaid(cancel)).Where(invoice => invoice.DueOn < today && !paid.Has(invoice.Order)).Take(limit);
        foreach (var invoice in due)
        {
            var reminder = new Reminder(invoice.Order, invoice.Amount);
            await billing.Remind(reminder, cancel);
            await announcer.Sent(reminder, cancel);
        }
    }
}

public sealed class RemindersJob(SendReminders send) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await send.RunAsync(context.MergedJobDataMap.GetInt("limit"), cancellationToken);
}
