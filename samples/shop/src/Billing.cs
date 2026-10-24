namespace Shop;

public sealed record Invoice(int Order, int Amount, DateOnly DueOn);

public sealed class Billing(HttpClient http)
{
    public async Task<Invoice> InvoiceOf(int order, CancellationToken cancel) =>
        await http.GetFromJsonAsync<Invoice>($"/invoices/{order}", cancel)
        ?? throw new InvalidOperationException($"billing answered no invoice for order {order}");
}
