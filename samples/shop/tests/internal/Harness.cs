using Specrig;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Shop.Specs.Internal;

public sealed class Harness : Harness<Program>
{
    public override string Service => "shop";

    public Backend Billing { get; } = new("https://billing.shop.test")
    {
        Setting = "Billing:BaseUrl",
        Contract = "../docs/billing.yaml",
        Failure = "Error",
    };

    public GrpcBackend Customers { get; } = new("https://customers.shop.test", Shop.Customers.Customers.Descriptor)
    {
        Setting = "Customers:Address",
    };

    public Postgres Database { get; } = new("ConnectionStrings:Shop");
}
