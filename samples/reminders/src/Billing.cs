using System.Net.Http.Json;

namespace Reminders;

public sealed record Invoice(int Order, int Amount, DateOnly DueOn);

public sealed record Reminder(int Order, int Amount);

public sealed class Billing(HttpClient http)
{
    public async Task<IReadOnlyList<Invoice>> Unpaid(CancellationToken cancel) =>
        await http.GetFromJsonAsync<List<Invoice>>("/invoices?status=unpaid", cancel) ?? [];

    public async Task Remind(Reminder reminder, CancellationToken cancel) =>
        (await http.PostAsJsonAsync("/reminders", reminder, cancel)).EnsureSuccessStatusCode();
}
