using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Specrig.SqliteTests;

public class SqliteTests
{
    const string Seeded = """
        create table states (id integer primary key autoincrement, name text not null);
        insert into states (name) values ('new'), ('active'), ('closed');
        create table categories (id integer primary key, parent_id integer references categories(id), name text not null, name_length int generated always as (length(name)) stored);
        insert into categories (parent_id, name) values (null, 'Master'), (1, 'Child');
        create table vendors (id integer primary key, state_id int not null references states(id) on delete restrict, name text not null);
        insert into vendors (state_id, name) values (1, 'R1');
        create table audit (id integer primary key, what text not null);
        create trigger vendors_audited after insert on vendors begin insert into audit (what) values (new.name); end;
        create trigger vendors_forgotten after delete on vendors begin insert into audit (what) values ('-' || old.name); end;
        create table orders (id integer primary key autoincrement, vendor_id int not null references vendors(id));
        """;

    static async Task<Database> Migrated(string service, string schema, params IReadOnlyList<string> keep)
    {
        var sqlite = new Sqlite("ConnectionStrings:Shop") { Keep = keep };
        var database = new Database(sqlite, await sqlite.Provision(service, CancellationToken.None));
        await database.Apply(schema);
        await using var services = new ServiceCollection().BuildServiceProvider();
        await sqlite.Migrate(services, CancellationToken.None);
        return database;
    }

    static string Refused(string write, string table) => $"SQLite Error 19: 'specrig: {write} on {table} is refused'.";

    sealed class Database(Sqlite sqlite, string address) : IDisposable
    {
        public Sqlite Sqlite => sqlite;

        public string Address => address;

        public Task Reset() => sqlite.Reset(CancellationToken.None);

        public Task Arm() => sqlite.Arm(CancellationToken.None);

        public async Task<SqliteException?> Attempt(string sql)
        {
            try
            {
                await Apply(sql);
                return null;
            }
            catch (SqliteException e)
            {
                return e;
            }
        }

        public async Task Rolled(string statement)
        {
            await using var connection = new SqliteConnection(address);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = new SqliteCommand(statement, connection, (SqliteTransaction)transaction);
            await command.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }

        public async Task Apply(string sql)
        {
            await using var connection = new SqliteConnection(address);
            await connection.OpenAsync();
            await using var command = new SqliteCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<List<T>> Column<T>(string query)
        {
            await using var connection = new SqliteConnection(address);
            await connection.OpenAsync();
            await using var command = new SqliteCommand(query, connection);
            await using var reader = await command.ExecuteReaderAsync();
            var column = new List<T>();
            while (await reader.ReadAsync())
            {
                column.Add(reader.GetFieldValue<T>(0));
            }
            return column;
        }

        public void Dispose() => sqlite.Dispose();
    }

    sealed class Shop(DbContextOptions<Shop> options) : DbContext(options)
    {
        public DbSet<State> States => Set<State>();

        protected override void OnModelCreating(ModelBuilder model) =>
            model.Entity<State>().HasData(new State { Id = 1, Name = "new" }, new State { Id = 2, Name = "active" });
    }

    sealed class State
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";
    }

    static async Task<(Sqlite Sqlite, ServiceProvider Services)> Booted(string service, Action<Sqlite>? before = null)
    {
        var sqlite = new Sqlite("ConnectionStrings:Shop");
        before?.Invoke(sqlite);
        var address = await sqlite.Provision(service, CancellationToken.None);
        var services = new ServiceCollection().AddDbContext<Shop>(options => options.UseSqlite(address));
        sqlite.Configure(services);
        var provider = services.BuildServiceProvider();
        await sqlite.Migrate(provider, CancellationToken.None);
        await sqlite.Reset(CancellationToken.None);
        return (sqlite, provider);
    }

    static async Task<List<string>> Names(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Shop>().States.OrderBy(s => s.Id).Select(s => s.Name).ToListAsync();
    }

    static async Task Add(IServiceProvider services, string name)
    {
        await using var scope = services.CreateAsyncScope();
        var shop = scope.ServiceProvider.GetRequiredService<Shop>();
        shop.States.Add(new State { Name = name });
        await shop.SaveChangesAsync();
    }

    [Fact]
    public async Task A_reset_puts_back_the_rows_the_migrations_left_and_drops_the_case_s_own()
    {
        using var database = await Migrated("reset_back", Seeded);
        await database.Apply("insert into states (name) values ('extra')");
        await database.Apply("update vendors set name = 'renamed'");
        await database.Apply("delete from categories where parent_id is not null");
        await database.Apply("insert into orders (vendor_id) values (1)");

        await database.Reset();

        Assert.Equal(["new", "active", "closed"], await database.Column<string>("select name from states order by id"));
        Assert.Equal(["Master", "Child"], await database.Column<string>("select name from categories order by id"));
        Assert.Equal(["R1"], await database.Column<string>("select name from vendors"));
        Assert.Empty(await database.Column<int>("select id from orders"));
    }

    [Fact]
    public async Task Ids_go_on_after_the_rows_the_migrations_left_and_start_over_where_they_left_none()
    {
        using var database = await Migrated("reset_ids", Seeded);
        await database.Apply("insert into states (name) values ('extra')");
        await database.Apply("insert into categories (name) values ('Extra')");
        await database.Apply("insert into vendors (state_id, name) values (1, 'R2')");
        await database.Apply("insert into orders (vendor_id) values (1)");

        await database.Reset();

        Assert.Equal([4], await database.Column<int>("insert into states (name) values ('next') returning id"));
        Assert.Equal([3], await database.Column<int>("insert into categories (name) values ('Next') returning id"));
        Assert.Equal([2], await database.Column<int>("insert into vendors (state_id, name) values (1, 'R2') returning id"));
        Assert.Equal([1], await database.Column<int>("insert into orders (vendor_id) values (1) returning id"));
    }

    [Fact]
    public async Task A_gap_the_migrations_left_in_the_ids_is_kept()
    {
        using var database = await Migrated("reset_gap", """
            create table states (id integer primary key autoincrement, name text not null);
            insert into states (name) values ('new'), ('active'), ('dropped');
            delete from states where name = 'dropped';
            """);
        await database.Apply("insert into states (name) values ('extra')");

        await database.Reset();

        Assert.Equal([4], await database.Column<int>("insert into states (name) values ('next') returning id"));
    }

    [Fact]
    public async Task Tables_that_reference_each_other_come_back()
    {
        using var database = await Migrated("reset_cycle", """
            create table teams (id int primary key, captain_id int references players(id));
            create table players (id int primary key, team_id int not null references teams(id));
            insert into teams (id) values (1);
            insert into players (id, team_id) values (1, 1);
            update teams set captain_id = 1;
            """);
        await database.Apply("insert into teams (id) values (2)");

        await database.Reset();

        Assert.Equal([1], await database.Column<int>("select captain_id from teams"));
        Assert.Equal([1], await database.Column<int>("select team_id from players"));
    }

    [Fact]
    public async Task A_reset_fires_no_trigger_and_keeps_them()
    {
        using var database = await Migrated("reset_trigger", Seeded);
        await database.Apply("update vendors set name = 'renamed'");

        await database.Reset();

        Assert.Empty(await database.Column<string>("select what from audit"));
        await database.Apply("insert into vendors (state_id, name) values (1, 'R2')");
        Assert.Equal(["R2"], await database.Column<string>("select what from audit"));
    }

    [Fact]
    public async Task Foreign_keys_are_checked_after_a_reset()
    {
        using var database = await Migrated("reset_keys", Seeded);
        await database.Apply("insert into orders (vendor_id) values (1)");

        await database.Reset();

        Assert.Equal("SQLite Error 19: 'FOREIGN KEY constraint failed'.", (await database.Attempt("insert into orders (vendor_id) values (99)"))?.Message);
    }

    [Fact]
    public async Task A_stored_generated_column_comes_back_recomputed()
    {
        using var database = await Migrated("reset_generated", Seeded);
        await database.Apply("update categories set name = 'Renamed' where id = 1");

        await database.Reset();

        Assert.Equal([6, 5], await database.Column<int>("select name_length from categories order by id"));
    }

    [Fact]
    public async Task Migrations_that_leave_no_tables_leave_nothing_to_put_back()
    {
        using var database = await Migrated("reset_none", "select 1");

        await database.Reset();
    }

    [Fact]
    public async Task A_kept_table_is_left_as_the_case_left_it()
    {
        using var database = await Migrated("reset_keep", Seeded, "states");
        await database.Apply("insert into states (name) values ('extra')");

        await database.Reset();

        Assert.Equal(["new", "active", "closed", "extra"], await database.Column<string>("select name from states order by id"));
        Assert.Equal([5], await database.Column<int>("insert into states (name) values ('next') returning id"));
    }

    [Fact]
    public async Task The_migration_history_is_left_as_it_is()
    {
        using var database = await Migrated("reset_history", """
            create table __EFMigrationsHistory (MigrationId text primary key, ProductVersion text not null);
            insert into __EFMigrationsHistory values ('20261023152955_Orders', '10.0.13');
            create table orders (id integer primary key autoincrement);
            """);
        await database.Apply("insert into __EFMigrationsHistory values ('20261111120000_Later', '10.0.15')");
        await database.Apply("insert into orders default values");

        await database.Reset();

        Assert.Equal(["20261023152955_Orders", "20261111120000_Later"],
            await database.Column<string>("select MigrationId from __EFMigrationsHistory order by MigrationId"));
        Assert.Empty(await database.Column<int>("select id from orders"));
    }

    [Fact]
    public async Task A_service_named_like_the_copies_keeps_its_own_file()
    {
        using var database = await Migrated("baseline", Seeded);
        await database.Apply("insert into states (name) values ('extra')");

        await database.Reset();

        Assert.Equal(["new", "active", "closed"], await database.Column<string>("select name from states order by id"));
    }

    [Fact]
    public async Task The_rows_a_context_seeds_come_back()
    {
        var (sqlite, services) = await Booted("context_seed");
        using var _ = sqlite;
        await using var __ = services;
        await Add(services, "closed");

        await sqlite.Reset(CancellationToken.None);

        Assert.Equal(["new", "active"], await Names(services));
    }

    [Fact]
    public async Task A_refused_table_refuses_every_write_and_still_reads()
    {
        using var database = await Migrated("refuse_all", Seeded);

        await database.Sqlite.Refuse("vendors");

        Assert.Equal(Refused("insert", "vendors"), (await database.Attempt("insert into vendors (state_id, name) values (1, 'R2')"))?.Message);
        Assert.Equal(Refused("update", "vendors"), (await database.Attempt("update vendors set name = 'renamed'"))?.Message);
        Assert.Equal(Refused("delete", "vendors"), (await database.Attempt("delete from vendors"))?.Message);
        Assert.Equal(["R1"], await database.Column<string>("select name from vendors"));
    }

    [Fact]
    public async Task A_write_that_returns_rows_is_refused_too()
    {
        using var database = await Migrated("refuse_returning", Seeded);
        await database.Sqlite.Refuse("states");

        var refused = await Assert.ThrowsAsync<SqliteException>(() => database.Column<int>("insert into states (name) values ('extra') returning id"));

        Assert.Equal(Refused("insert", "states"), refused.Message);
    }

    [Fact]
    public async Task A_refusal_leaves_the_other_tables_alone()
    {
        using var database = await Migrated("refuse_elsewhere", Seeded);

        await database.Sqlite.Refuse("vendors");

        Assert.Null(await database.Attempt("insert into states (name) values ('extra')"));
    }

    [Fact]
    public async Task A_refusal_leaves_the_writes_it_does_not_name_alone()
    {
        using var database = await Migrated("refuse_named", Seeded);

        await database.Sqlite.Refuse("vendors", Write.Delete);

        Assert.Null(await database.Attempt("update vendors set name = 'renamed'"));
        Assert.Equal(Refused("delete", "vendors"), (await database.Attempt("delete from vendors"))?.Message);
    }

    [Fact]
    public async Task A_table_that_is_not_there_is_named_in_the_failure()
    {
        using var database = await Migrated("refuse_missing", Seeded);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => database.Sqlite.Refuse("nowhere"));

        Assert.Equal("sqlite: cannot refuse writes to nowhere: SQLite Error 1: 'no such table: main.nowhere'.", refused.Message);
    }

    [Fact]
    public async Task A_quota_lets_that_many_rows_through_whichever_write_they_are()
    {
        using var database = await Migrated("refuse_quota", Seeded);

        await database.Sqlite.RefuseAfter("states", 1);

        Assert.Null(await database.Attempt("update states set name = 'renamed' where id = 1"));
        Assert.Equal(Refused("insert", "states"), (await database.Attempt("insert into states (name) values ('second')"))?.Message);
    }

    [Fact]
    public async Task A_quota_counts_the_rows_of_a_statement()
    {
        using var database = await Migrated("refuse_rows", Seeded);
        await database.Sqlite.RefuseAfter("states", 2, Write.Insert);

        var refused = await database.Attempt("insert into states (name) values ('first'), ('second'), ('third')");

        Assert.NotNull(refused);
        Assert.Equal(["new", "active", "closed"], await database.Column<string>("select name from states order by id"));
    }

    [Fact]
    public async Task A_rolled_back_row_gives_its_place_back()
    {
        using var database = await Migrated("refuse_rolled", Seeded);
        await database.Sqlite.RefuseAfter("states", 1);

        await database.Rolled("insert into states (name) values ('undone')");

        Assert.Null(await database.Attempt("insert into states (name) values ('kept')"));
    }

    [Fact]
    public async Task Refusing_again_replaces_the_quota()
    {
        using var database = await Migrated("refuse_again", Seeded);
        await database.Sqlite.Refuse("states");

        await database.Sqlite.RefuseAfter("states", 1);

        Assert.Null(await database.Attempt("insert into states (name) values ('first')"));
    }

    [Fact]
    public async Task A_refusal_set_before_the_boot_waits_for_the_start()
    {
        using var sqlite = new Sqlite("ConnectionStrings:Shop");
        await sqlite.Refuse("vendors", Write.Insert);
        using var database = new Database(sqlite, await sqlite.Provision("refuse_early", CancellationToken.None));
        await database.Apply(Seeded);
        await using var services = new ServiceCollection().BuildServiceProvider();
        await sqlite.Migrate(services, CancellationToken.None);
        await database.Reset();
        Assert.Null(await database.Attempt("insert into vendors (state_id, name) values (1, 'R2')"));

        await database.Arm();

        Assert.Equal(Refused("insert", "vendors"), (await database.Attempt("insert into vendors (state_id, name) values (1, 'R3')"))?.Message);
        Assert.Equal(["R1", "R2"], await database.Column<string>("select name from vendors order by id"));
    }

    [Fact]
    public async Task A_reset_lifts_the_refusals()
    {
        using var database = await Migrated("refuse_reset", Seeded);
        await database.Sqlite.Refuse("vendors");

        await database.Reset();

        Assert.Null(await database.Attempt("insert into vendors (state_id, name) values (1, 'R2')"));
        Assert.Empty(await database.Column<string>("select name from sqlite_schema where name like 'specrig%'"));
    }

    [Fact]
    public async Task A_context_meets_a_database_that_cannot_be_opened_until_the_heal_and_the_rows_stay()
    {
        var (sqlite, services) = await Booted("fail_heal");
        using var _ = sqlite;
        await using var __ = services;
        await sqlite.Arm(CancellationToken.None);
        await Add(services, "closed");

        sqlite.FailWith(null);
        var refused = await Assert.ThrowsAsync<SqliteException>(() => Names(services));
        Assert.Equal((14, "SQLite Error 14: 'unable to open database file'."), (refused.SqliteErrorCode, refused.Message));

        await sqlite.Heal();

        Assert.Equal(["new", "active", "closed"], await Names(services));
    }

    [Fact]
    public async Task A_fault_set_before_the_boot_waits_for_the_start()
    {
        var (sqlite, services) = await Booted("fail_early", sqlite => sqlite.FailWith(new InvalidOperationException("the disk is full")));
        using var _ = sqlite;
        await using var __ = services;
        Assert.Equal(["new", "active"], await Names(services));

        await sqlite.Arm(CancellationToken.None);

        Assert.Equal("the disk is full", (await Assert.ThrowsAsync<InvalidOperationException>(() => Names(services))).Message);
    }

    [Fact]
    public async Task A_heal_lifts_the_refusals_and_keeps_the_rows()
    {
        using var database = await Migrated("refuse_heal", Seeded);
        await database.Arm();
        await database.Apply("insert into states (name) values ('extra')");
        await database.Sqlite.Refuse("vendors");

        await database.Sqlite.Heal();

        Assert.Null(await database.Attempt("insert into vendors (state_id, name) values (1, 'R2')"));
        Assert.Equal(["new", "active", "closed", "extra"], await database.Column<string>("select name from states order by id"));
    }

    [Fact]
    public async Task A_heal_before_the_boot_drops_what_was_waiting_for_it()
    {
        using var sqlite = new Sqlite("ConnectionStrings:Shop");
        await sqlite.Refuse("vendors");
        sqlite.FailWith(null);
        using var database = new Database(sqlite, await sqlite.Provision("refuse_heal_early", CancellationToken.None));
        await database.Apply(Seeded);
        await using var services = new ServiceCollection().BuildServiceProvider();
        await sqlite.Migrate(services, CancellationToken.None);

        await sqlite.Heal();
        await sqlite.Reset(CancellationToken.None);
        await sqlite.Arm(CancellationToken.None);

        Assert.Null(await database.Attempt("insert into vendors (state_id, name) values (1, 'R2')"));
    }

    [Fact]
    public async Task The_end_of_the_run_removes_the_database()
    {
        var file = SqliteFile.Start("down");
        await using (var connection = new SqliteConnection(file.Address))
        {
            await connection.OpenAsync();
        }
        var dir = Path.GetDirectoryName(new SqliteConnectionStringBuilder(file.Address).DataSource)!;
        Assert.True(Directory.Exists(dir));

        await file.Down();

        Assert.False(Directory.Exists(dir));
    }
}
