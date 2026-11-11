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
        public Postgres Postgres => postgres;

        public Task Reset() => postgres.Reset(CancellationToken.None);

        public Task Arm() => postgres.Arm(CancellationToken.None);

        public async Task<PostgresException?> Attempt(string sql)
        {
            try
            {
                await Apply(sql);
                return null;
            }
            catch (PostgresException e)
            {
                return e;
            }
        }

        public async Task<PostgresException?> Transaction(params string[] statements)
        {
            await using var connection = new NpgsqlConnection(address);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var statement in statements)
            {
                await using var command = new NpgsqlCommand(statement, connection, transaction);
                await command.ExecuteNonQueryAsync();
            }
            try
            {
                await transaction.CommitAsync();
                return null;
            }
            catch (PostgresException e)
            {
                return e;
            }
        }

        public async Task Rolled(string statement)
        {
            await using var connection = new NpgsqlConnection(address);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand(statement, connection, transaction);
            await command.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }

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

    [Fact]
    public async Task A_refused_table_refuses_every_write_and_still_reads()
    {
        using var database = await Migrated("refuse_all", Seeded);

        await database.Postgres.Refuse("vendors");

        Assert.Equal("specrig: insert on vendors is refused", (await database.Attempt("insert into vendors (state_id, name) values (1, 'R2')"))?.MessageText);
        Assert.Equal("specrig: update on vendors is refused", (await database.Attempt("update vendors set name = 'renamed'"))?.MessageText);
        Assert.Equal("specrig: delete on vendors is refused", (await database.Attempt("delete from vendors"))?.MessageText);
        Assert.Equal(["R1"], await database.Column<string>("select name from vendors"));
    }

    [Fact]
    public async Task A_write_that_returns_rows_is_refused_too()
    {
        using var database = await Migrated("refuse_returning", Seeded);
        await database.Postgres.Refuse("states");

        var refused = await Assert.ThrowsAsync<PostgresException>(() => database.Column<int>("insert into states (name) values ('extra') returning id"));

        Assert.Equal("specrig: insert on states is refused", refused.MessageText);
    }

    [Fact]
    public async Task A_refusal_leaves_the_other_tables_alone()
    {
        using var database = await Migrated("refuse_elsewhere", Seeded);

        await database.Postgres.Refuse("vendors");

        Assert.Null(await database.Attempt("insert into states (name) values ('extra')"));
    }

    [Fact]
    public async Task A_refusal_leaves_the_writes_it_does_not_name_alone()
    {
        using var database = await Migrated("refuse_named", Seeded);

        await database.Postgres.Refuse("vendors", Write.Delete);

        Assert.Null(await database.Attempt("update vendors set name = 'renamed'"));
        Assert.Equal("specrig: delete on vendors is refused", (await database.Attempt("delete from vendors"))?.MessageText);
    }

    [Fact]
    public async Task A_table_in_a_schema_of_its_own_is_named_with_it()
    {
        using var database = await Migrated("refuse_schema", "create schema shop; create table shop.\"Orders\" (id int)");

        await database.Postgres.Refuse("shop.Orders", Write.Insert);

        Assert.Equal("specrig: insert on Orders is refused", (await database.Attempt("insert into shop.\"Orders\" values (1)"))?.MessageText);
    }

    [Fact]
    public async Task A_table_that_is_not_there_is_named_in_the_failure()
    {
        using var database = await Migrated("refuse_missing", Seeded);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => database.Postgres.Refuse("nowhere"));

        Assert.Equal("postgres: cannot refuse writes to nowhere: relation \"nowhere\" does not exist", refused.Message);
    }

    [Fact]
    public async Task Only_a_write_can_be_refused()
    {
        var postgres = new Postgres("ConnectionStrings:Shop");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => postgres.Refuse("vendors", (Write)3));
    }

    [Fact]
    public async Task A_quota_lets_that_many_rows_through()
    {
        using var database = await Migrated("refuse_quota", Seeded);

        await database.Postgres.RefuseAfter("states", 1);

        Assert.Null(await database.Attempt("insert into states (name) values ('first')"));
        Assert.Equal("specrig: insert on states is refused", (await database.Attempt("insert into states (name) values ('second')"))?.MessageText);
    }

    [Fact]
    public async Task A_quota_counts_the_rows_of_a_statement()
    {
        using var database = await Migrated("refuse_rows", Seeded);
        await database.Postgres.RefuseAfter("states", 2, Write.Insert);

        var refused = await database.Attempt("insert into states (name) values ('first'), ('second'), ('third')");

        Assert.NotNull(refused);
        Assert.Equal(["new", "active", "closed"], await database.Column<string>("select name from states order by id"));
    }

    [Fact]
    public async Task A_rolled_back_row_gives_its_place_back()
    {
        using var database = await Migrated("refuse_rolled", Seeded);
        await database.Postgres.RefuseAfter("states", 1);

        await database.Rolled("insert into states (name) values ('undone')");

        Assert.Null(await database.Attempt("insert into states (name) values ('kept')"));
    }

    [Fact]
    public async Task Refusing_again_replaces_the_quota()
    {
        using var database = await Migrated("refuse_again", Seeded);
        await database.Postgres.Refuse("states");

        await database.Postgres.RefuseAfter("states", 1);

        Assert.Null(await database.Attempt("insert into states (name) values ('first')"));
    }

    [Fact]
    public async Task A_commit_refusal_lets_the_statements_through_and_fails_the_commit()
    {
        using var database = await Migrated("refuse_commit", Seeded);
        await database.Postgres.RefuseCommit("states");

        var refused = await database.Transaction("insert into states (name) values ('extra')");

        Assert.Equal("specrig: insert on states is refused", refused?.MessageText);
        Assert.Equal(["new", "active", "closed"], await database.Column<string>("select name from states order by id"));
    }

    [Fact]
    public async Task A_commit_refusal_leaves_a_transaction_that_did_not_write_the_table_alone()
    {
        using var database = await Migrated("refuse_commit_elsewhere", Seeded);
        await database.Postgres.RefuseCommit("states");

        var refused = await database.Transaction("select name from states", "insert into vendors (state_id, name) values (1, 'R2')");

        Assert.Null(refused);
    }

    [Fact]
    public async Task A_refusal_set_before_the_boot_waits_for_the_start()
    {
        using var postgres = new Postgres("ConnectionStrings:Shop");
        await postgres.Refuse("vendors", Write.Insert);
        using var database = new Database(postgres, await postgres.Provision("refuse_early", CancellationToken.None));
        await database.Apply(Seeded);
        await using var services = new ServiceCollection().BuildServiceProvider();
        await postgres.Migrate(services, CancellationToken.None);
        await database.Reset();
        Assert.Null(await database.Attempt("insert into vendors (state_id, name) values (1, 'R2')"));

        await database.Arm();

        Assert.Equal("specrig: insert on vendors is refused", (await database.Attempt("insert into vendors (state_id, name) values (1, 'R3')"))?.MessageText);
        Assert.Equal(["R1", "R2"], await database.Column<string>("select name from vendors order by id"));
    }

    [Fact]
    public async Task A_fault_set_before_the_boot_waits_for_the_start()
    {
        using var postgres = new Postgres("ConnectionStrings:Shop");
        postgres.FailWith(null);
        using var database = new Database(postgres, await postgres.Provision("fail_early", CancellationToken.None));
        await database.Apply(Seeded);
        await using var services = new ServiceCollection().BuildServiceProvider();
        await postgres.Migrate(services, CancellationToken.None);
        await database.Reset();
        Assert.Equal(["R1"], await database.Column<string>("select name from vendors"));

        await database.Arm();

        await Assert.ThrowsAnyAsync<NpgsqlException>(() => database.Apply("select 1"));
    }

    [Fact]
    public async Task A_reset_lifts_the_refusals()
    {
        using var database = await Migrated("refuse_reset", Seeded);
        await database.Postgres.Refuse("vendors");
        await database.Postgres.RefuseCommit("states");

        await database.Reset();

        Assert.Null(await database.Attempt("insert into vendors (state_id, name) values (1, 'R2')"));
        Assert.Null(await database.Transaction("insert into states (name) values ('extra')"));
        Assert.Equal([false], await database.Column<bool>("select exists (select 1 from pg_namespace where nspname = 'specrig_refusal')"));
    }

    [Fact]
    public async Task A_heal_lifts_the_refusals_and_the_fault_and_keeps_the_rows()
    {
        using var database = await Migrated("refuse_heal", Seeded);
        await database.Arm();
        await database.Apply("insert into states (name) values ('extra')");
        await database.Postgres.Refuse("vendors");
        database.Postgres.FailWith(null);
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => database.Apply("select 1"));

        await database.Postgres.Heal();

        Assert.Null(await database.Attempt("insert into vendors (state_id, name) values (1, 'R2')"));
        Assert.Equal(["new", "active", "closed", "extra"], await database.Column<string>("select name from states order by id"));
    }

    [Fact]
    public async Task A_heal_before_the_boot_drops_what_was_waiting_for_it()
    {
        using var postgres = new Postgres("ConnectionStrings:Shop");
        await postgres.Refuse("vendors");
        postgres.FailWith(null);
        using var database = new Database(postgres, await postgres.Provision("refuse_heal_early", CancellationToken.None));
        await database.Apply(Seeded);
        await using var services = new ServiceCollection().BuildServiceProvider();
        await postgres.Migrate(services, CancellationToken.None);

        await postgres.Heal();
        await postgres.Reset(CancellationToken.None);
        await postgres.Arm(CancellationToken.None);

        Assert.Null(await database.Attempt("insert into vendors (state_id, name) values (1, 'R2')"));
        Assert.Equal(["R1", "R2"], await database.Column<string>("select name from vendors order by id"));
    }
}
