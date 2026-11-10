using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Specrig.Hosting.Tests;

public class PostgresTests
{
    const string Seeded = """
        create table states (id int generated always as identity primary key, name text not null);
        insert into states (name) values ('new'), ('active'), ('closed');
        create table categories (id bigserial primary key, parent_id bigint references categories(id), name text not null, name_length int generated always as (length(name)) stored);
        insert into categories (parent_id, name) values (null, 'Master'), (1, 'Child');
        create table vendors (id serial primary key, state_id int not null references states(id), name text not null);
        insert into vendors (state_id, name) values (1, 'R1');
        create table audit (id serial primary key, what text not null);
        create function audited() returns trigger language plpgsql as $$ begin insert into audit (what) values (new.name); return new; end $$;
        create trigger vendors_audited after insert on vendors for each row execute function audited();
        create table orders (id serial primary key, vendor_id int not null references vendors(id));
        """;

    static async Task<Database> Migrated(string service, string schema, params IReadOnlyList<string> keep)
    {
        var postgres = new Postgres("ConnectionStrings:Shop") { Keep = keep };
        var database = new Database(postgres, await postgres.Provision(service, CancellationToken.None));
        await database.Apply(schema);
        await using var services = new ServiceCollection().BuildServiceProvider();
        await postgres.Migrate(services, CancellationToken.None);
        return database;
    }

    sealed class Database(Postgres postgres, string address) : IDisposable
    {
        public Task Reset() => postgres.Reset(CancellationToken.None);

        public async Task Apply(string sql)
        {
            await using var connection = new NpgsqlConnection(address);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<List<T>> Column<T>(string query)
        {
            await using var connection = new NpgsqlConnection(address);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(query, connection);
            await using var reader = await command.ExecuteReaderAsync();
            var column = new List<T>();
            while (await reader.ReadAsync())
            {
                column.Add(reader.GetFieldValue<T>(0));
            }
            return column;
        }

        public void Dispose() => postgres.Dispose();
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
        await database.Apply("insert into vendors (state_id, name) values (1, 'R2')");
        await database.Apply("insert into orders (vendor_id) values (1)");

        await database.Reset();

        Assert.Equal([4], await database.Column<int>("insert into states (name) values ('next') returning id"));
        Assert.Equal([3L], await database.Column<long>("insert into categories (name) values ('Next') returning id"));
        Assert.Equal([2], await database.Column<int>("insert into vendors (state_id, name) values (1, 'R2') returning id"));
        Assert.Equal([1], await database.Column<int>("insert into orders (vendor_id) values (1) returning id"));
    }

    [Fact]
    public async Task Tables_that_reference_each_other_come_back()
    {
        using var database = await Migrated("reset_cycle", """
            create table teams (id int primary key, captain_id int);
            create table players (id int primary key, team_id int not null references teams(id));
            alter table teams add foreign key (captain_id) references players(id);
            insert into teams (id) values (1);
            insert into players (id, team_id) values (1, 1);
            update teams set captain_id = 1;
            """);

        await database.Reset();

        Assert.Equal([1], await database.Column<int>("select captain_id from teams"));
        Assert.Equal([1], await database.Column<int>("select team_id from players"));
    }

    [Fact]
    public async Task A_reset_fires_no_trigger()
    {
        using var database = await Migrated("reset_trigger", Seeded);

        await database.Reset();

        Assert.Empty(await database.Column<string>("select what from audit"));
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
    public async Task Migrations_that_leave_no_rows_leave_nothing_aside()
    {
        using var database = await Migrated("reset_empty", "create table orders (id serial primary key)");
        await database.Apply("insert into orders default values");

        await database.Reset();

        Assert.Equal([1], await database.Column<int>("insert into orders default values returning id"));
        Assert.Equal([false], await database.Column<bool>("select exists (select 1 from pg_namespace where nspname = 'specrig_baseline')"));
    }

    [Fact]
    public async Task A_partitioned_table_comes_back_once()
    {
        using var database = await Migrated("reset_partitioned", """
            create table events (id bigserial, at date not null, name text not null) partition by range (at);
            create table events_2026 partition of events for values from ('2026-01-01') to ('2027-01-01');
            insert into events (at, name) values ('2026-03-01', 'seeded');
            """);

        await database.Reset();

        Assert.Equal(["seeded"], await database.Column<string>("select name from events"));
        Assert.Equal([2L], await database.Column<long>("insert into events (at, name) values ('2026-04-01', 'next') returning id"));
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
    public async Task Tables_named_up_to_the_identifier_limit_come_back_each_as_it_was()
    {
        var first = new string('t', 62) + "1";
        var second = new string('t', 62) + "2";
        using var database = await Migrated("reset_long", $"""
            create table {first} (name text not null);
            insert into {first} values ('first');
            create table {second} (name text not null);
            insert into {second} values ('second');
            """);

        await database.Reset();

        Assert.Equal(["first"], await database.Column<string>($"select name from {first}"));
        Assert.Equal(["second"], await database.Column<string>($"select name from {second}"));
    }

    [Fact]
    public async Task A_restore_that_cannot_work_fails_the_migration_not_a_case()
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(() => Migrated("reset_refused", """
            create table limits (value int not null);
            insert into limits values (0);
            alter table limits add check (value > 0) not valid;
            """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
    }
}
