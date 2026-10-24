using Specrig;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Reminders.Specs.Internal;

public sealed class Harness : Harness<Program>
{
    public override string Service => "reminders";

    public Backend Billing { get; } = new("https://billing.reminders.test")
    {
        Setting = "Billing:BaseUrl",
        Contract = "../docs/billing.yaml",
        Failure = "Error",
    };

    public RabbitBus Bus { get; } = new("ConnectionStrings:Rabbit");

    protected override Settings Constants => new() { ["Reminders:Limit"] = "1" };

    protected override Workers Running => [typeof(InvoicesPaid)];
}
