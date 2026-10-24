using Microsoft.EntityFrameworkCore;

namespace Shop;

public sealed record Order(int Id);

public sealed record OrderView(int Id, int ToPay);

public interface IOrders
{
    Task<Order?> Find(int id, CancellationToken cancel);

    Task Save(Order order, CancellationToken cancel);
}

public sealed class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder model) => model.Entity<Order>(order =>
    {
        order.ToTable("orders");
        order.Property(o => o.Id).ValueGeneratedNever();
    });
}

public sealed class Orders(ShopDb db) : IOrders
{
    public async Task<Order?> Find(int id, CancellationToken cancel) => await db.Orders.FindAsync([id], cancel);

    public async Task Save(Order order, CancellationToken cancel)
    {
        db.Orders.Add(order);
        await db.SaveChangesAsync(cancel);
    }
}
