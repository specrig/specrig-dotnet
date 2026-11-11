using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Starting;

namespace Specrig.Hosting.Tests;

public class StartingTests
{
    class StartingHarness(string builder = "web") : Rig
    {
        public override string Service => "starting";

        protected override Assembly Program => typeof(Seen).Assembly;

        protected override Settings Constants => new() { ["builder"] = builder };

        protected override Workers Running => [typeof(Reader)];

        public Postgres Database { get; } = new("ConnectionStrings:Starting");

        public async Task Leave(int id, string text)
        {
            await using var scope = Scope();
            scope.Get<StartingDb>().Notes.Add(new Note(id, text));
            await scope.Get<StartingDb>().SaveChangesAsync();
        }

        public async Task<List<string>> Notes()
        {
            await using var scope = Scope();
            return await scope.Get<StartingDb>().Notes.OrderBy(n => n.Id).Select(n => n.Text).ToListAsync();
        }
    }

    sealed class Seeding : StartingHarness
    {
        public int Seeded { get; private set; }

        protected override async Task Seed(IServiceProvider services)
        {
            Seeded++;
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StartingDb>();
            db.Notes.Add(new Note(1, "admin"));
            await db.SaveChangesAsync();
        }
    }

    sealed class Unseedable : StartingHarness
    {
        protected override Task Seed(IServiceProvider services) => throw new InvalidOperationException("no administrator");
    }

    sealed class Reaching : StartingHarness
    {
        protected override Task Seed(IServiceProvider services)
        {
            using var scope = Scope();
            return Task.CompletedTask;
        }
    }

    static async Task Left(string text)
    {
        using var last = new StartingHarness();
        await last.Leave(7, text);
    }

    [Theory]
    [InlineData("web")]
    [InlineData("generic")]
    [InlineData("default")]
    public async Task What_the_service_reads_when_it_starts_is_the_database_put_back(string builder)
    {
        await Left("left by the last case");
        Seen.Forget();
        using var next = new StartingHarness(builder);

        using var scope = next.Scope();

        Assert.Equal(["program: []", "hosted service: []"], Seen.Log);
    }

    [Fact]
    public async Task A_seed_is_written_into_the_database_just_put_back_before_the_service_starts()
    {
        await Left("left by the last case");
        Seen.Forget();
        using var next = new Seeding();

        Assert.Equal(["admin"], await next.Notes());

        Assert.Equal(["program: [admin]", "hosted service: [admin]"], Seen.Log);
        Assert.Equal(1, next.Seeded);
    }

    [Fact]
    public async Task A_fault_a_step_set_before_the_boot_waits_for_the_start_and_the_seed()
    {
        using var next = new Seeding();
        next.Database.FailWith(null);
        Seen.Forget();

        using var scope = next.Scope();

        Assert.Equal(["program: [admin]", "hosted service: [admin]"], Seen.Log);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(next.Notes);
        Assert.IsType<Npgsql.NpgsqlException>(refused.InnerException);
    }

    [Fact]
    public async Task A_command_s_host_does_not_ready_what_the_service_s_host_readied()
    {
        using var next = new Seeding();
        await next.Leave(2, "left by the case");
        Seen.Forget();

        Assert.Equal(0, await next.Command("read"));

        Assert.Equal(["program: [admin, left by the case]", "command: [admin, left by the case]"], Seen.Log);
        Assert.Equal(1, next.Seeded);
    }

    [Fact]
    public void A_seed_that_fails_fails_the_boot_with_what_it_threw()
    {
        using var next = new Unseedable();

        var refused = Assert.Throws<InvalidOperationException>(() => next.Scope());

        Assert.Equal("no administrator", refused.Message);
    }

    [Fact]
    public void A_seed_that_reaches_for_the_harness_is_told_to_use_its_services()
    {
        using var next = new Reaching();

        var refused = Assert.Throws<InvalidOperationException>(() => next.Scope());

        Assert.Equal("starting is starting: Seed writes through the services it is given, not through the harness", refused.Message);
    }
}
