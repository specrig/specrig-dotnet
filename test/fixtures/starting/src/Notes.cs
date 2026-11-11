using Microsoft.EntityFrameworkCore;

namespace Starting;

public sealed record Note(int Id, string Text);

public sealed class StartingDb(DbContextOptions<StartingDb> options) : DbContext(options)
{
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder model) => model.Entity<Note>(note =>
    {
        note.ToTable("notes");
        note.Property(n => n.Id).ValueGeneratedNever();
    });
}

public static class Seen
{
    static readonly List<string> seen = [];

    public static IReadOnlyList<string> Log
    {
        get
        {
            lock (seen)
            {
                return [.. seen];
            }
        }
    }

    public static void Forget()
    {
        lock (seen)
        {
            seen.Clear();
        }
    }

    public static async Task Read(string who, IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var notes = await scope.ServiceProvider.GetRequiredService<StartingDb>().Notes.OrderBy(n => n.Id).Select(n => n.Text).ToListAsync();
        lock (seen)
        {
            seen.Add($"{who}: [{string.Join(", ", notes)}]");
        }
    }
}

public sealed class Reader(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancel) => Seen.Read("hosted service", services);

    public Task StopAsync(CancellationToken cancel) => Task.CompletedTask;
}
