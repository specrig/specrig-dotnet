using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace Specrig.MySqlTests;

public class MySqlTests
{
    const string Seeded = """
        create table states (id int auto_increment primary key, name varchar(50) not null);
        insert into states (name) values ('new'), ('active'), ('closed');
        create table categories (
            id bigint auto_increment primary key,
            parent_id bigint references categories(id),
            name varchar(50) not null,
            name_length int generated always as (char_length(name)) stored,
            created_at datetime(6) not null default current_timestamp(6),
            foreign key (parent_id) references categories(id)
        );
        insert into categories (parent_id, name) values (null, 'Master');
        insert into categories (parent_id, name) values (1, 'Child');
        create table vendors (id int auto_increment primary key, state_id int not null, name varchar(50) not null, foreign key (state_id) references states(id) on delete restrict);
        insert into vendors (state_id, name) values (1, 'R1');
        create table audit (id int auto_increment primary key, what varchar(50) not null);
        create trigger vendors_audited after insert on vendors for each row insert into audit (what) values (new.name);
        create trigger vendors_forgotten after delete on vendors for each row insert into audit (what) values (concat('-', old.name));
        create table orders (id int auto_increment primary key, vendor_id int not null, foreign key (vendor_id) references vendors(id));
        """;

    static async Task<Database> Migrated(string service, string schema, params IReadOnlyList<string> keep)
    {
        var mysql = new Mysql("ConnectionStrings:Shop") { Keep = keep };
        var database = new Database(mysql, await mysql.Provision(service, CancellationToken.None));
        await database.Apply(schema);
        await using var services = new ServiceCollection().BuildServiceProvider();
        await mysql.Migrate(services, CancellationToken.None);
        return database;
    }

    static string Refused(string write, string table) => $"specrig: {write} on {table} is refused";

    sealed class Database(Mysql mysql, string address) : IDisposable
    {
        public Mysql Mysql => mysql;

        public Task Reset() => mysql.Reset(CancellationToken.None);

        public Task Arm() => mysql.Arm(CancellationToken.None);

        public async Task<MySqlException?> Attempt(string sql)
        {
            try
            {
                await Apply(sql);
                return null;
            }
            catch (MySqlException e)
            {
                return e;
            }
        }

        public async Task Rolled(string statement)
        {
            await using var connection = new MySqlConnection(address);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = new MySqlCommand(statement, connection, transaction);
            await command.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }

        public async Task Apply(string sql)
        {
            await using var connection = new MySqlConnection(address);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> Inserted(string sql)
        {
            await using var connection = new MySqlConnection(address);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
            return command.LastInsertedId;
        }

        public async Task<List<T>> Column<T>(string query)
        {
            await using var connection = new MySqlConnection(address);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(query, connection);
            await using var reader = await command.ExecuteReaderAsync();
            var column = new List<T>();
            while (await reader.ReadAsync())
            {
                column.Add(reader.GetFieldValue<T>(0));
            }
            return column;
        }

        public void Dispose() => mysql.Dispose();
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

    static async Task<(Mysql Mysql, ServiceProvider Services)> Booted(string service)
    {
        var mysql = new Mysql("ConnectionStrings:Shop");
        var address = await mysql.Provision(service, CancellationToken.None);
        var services = new ServiceCollection().AddDbContext<Shop>(options => options.UseMySql(address, new MySqlServerVersion(new Version(8, 4))));
        mysql.Configure(services);
        var provider = services.BuildServiceProvider();
        await mysql.Migrate(provider, CancellationToken.None);
        await mysql.Reset(CancellationToken.None);
        return (mysql, provider);
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
        await database.Apply("insert into vendors (state_id, name) values (1, 'R2')");
        await database.Apply("insert into orders (vendor_id) values (1)");

        await database.Reset();

        Assert.Equal(4, await database.Inserted("insert into states (name) values ('next')"));
        Assert.Equal(3, await database.Inserted("insert into categories (name) values ('Next')"));
        Assert.Equal(2, await database.Inserted("insert into vendors (state_id, name) values (1, 'R2')"));
        Assert.Equal(1, await database.Inserted("insert into orders (vendor_id) values (1)"));
    }

    [Fact]
    public async Task A_counter_a_row_since_deleted_moved_goes_back()
    {
        using var database = await Migrated("reset_counter", Seeded);
        await database.Apply("insert into states (name) values ('extra')");
        await database.Apply("delete from states where name = 'extra'");

        await database.Reset();

        Assert.Equal(4, await database.Inserted("insert into states (name) values ('next')"));
    }

    [Fact]
    public async Task A_gap_the_migrations_left_in_the_ids_is_kept()
    {
        using var database = await Migrated("reset_gap", """
            create table states (id int auto_increment primary key, name varchar(50) not null);
            insert into states (name) values ('new'), ('active'), ('dropped');
            delete from states where name = 'dropped';
            """);
        await database.Apply("insert into states (name) values ('extra')");

        await database.Reset();

        Assert.Equal(4, await database.Inserted("insert into states (name) values ('next')"));
    }

    [Fact]
    public async Task Tables_that_reference_each_other_come_back()
    {
        using var database = await Migrated("reset_cycle", """
            create table teams (id int primary key, captain_id int);
            create table players (id int primary key, team_id int not null, foreign key (team_id) references teams(id));
            alter table teams add foreign key (captain_id) references players(id);
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
        await database.Apply("delete from vendors where name = 'R2'");
        Assert.Equal(["R2", "-R2"], await database.Column<string>("select what from audit order by id"));
    }

    [Fact]
    public async Task A_trigger_a_failed_reset_lost_comes_back()
    {
        using var database = await Migrated("reset_lost", Seeded);
        await database.Apply("drop trigger vendors_audited");

        await database.Reset();

        await database.Apply("insert into vendors (state_id, name) values (1, 'R2')");
        Assert.Equal(["R2"], await database.Column<string>("select what from audit"));
    }

    [Fact]
    public async Task Triggers_come_back_in_the_order_they_fire()
    {
        using var database = await Migrated("reset_order", """
            create table labels (id int auto_increment primary key, name varchar(50) not null);
            insert into labels (name) values ('seeded');
            create trigger a_second before insert on labels for each row set new.name = concat(new.name, '-a');
            create trigger z_first before insert on labels for each row precedes a_second set new.name = concat(new.name, '-z');
            """);
        await database.Apply("insert into labels (name) values ('extra')");

        await database.Reset();

        await database.Apply("insert into labels (name) values ('next')");
        Assert.Equal(["seeded", "next-z-a"], await database.Column<string>("select name from labels order by id"));
    }

    [Fact]
    public async Task A_failed_reset_leaves_the_rows_and_no_lock_behind()
    {
        using var database = await Migrated("reset_failed", """
            create table states (id int auto_increment primary key, name varchar(50) not null);
            insert into states (name) values ('new'), ('active');
            """);
        await database.Apply("insert into states (name) values ('extra')");
        await database.Apply("drop table specrig_kept_0");

        await Assert.ThrowsAsync<MySqlException>(database.Reset);

        Assert.Null(await database.Attempt("set session innodb_lock_wait_timeout = 1; insert into states (name) values ('after')"));
        Assert.Equal(["new", "active", "extra", "after"], await database.Column<string>("select name from states order by id"));
    }

    [Fact]
    public async Task A_row_the_migrations_put_at_id_zero_comes_back_there()
    {
        using var database = await Migrated("reset_zero", """
            create table states (id int auto_increment primary key, name varchar(50) not null);
            insert into states (name) values ('none'), ('new');
            update states set id = 0 where name = 'none';
            """);
        await database.Apply("insert into states (name) values ('extra')");

        await database.Reset();

        Assert.Equal([0, 2], await database.Column<int>("select id from states order by id"));
    }

    [Fact]
    public async Task A_trigger_keeps_the_sql_mode_it_was_made_under()
    {
        using var database = await Migrated("reset_mode", """
            create table events (id int auto_increment primary key, name varchar(50) not null);
            insert into events (name) values ('seeded');
            create table copies (name varchar(3) not null);
            set session sql_mode = '';
            create trigger events_copied after insert on events for each row insert into copies (name) values (new.name);
            """);
        await database.Apply("insert into events (name) values ('extra')");

        await database.Reset();

        Assert.Null(await database.Attempt("insert into events (name) values ('longer')"));
        Assert.Equal(["lon"], await database.Column<string>("select name from copies"));
    }

    [Fact]
    public async Task Foreign_keys_are_checked_after_a_reset()
    {
        using var database = await Migrated("reset_keys", Seeded);
        await database.Apply("insert into orders (vendor_id) values (1)");

        await database.Reset();

        Assert.Equal(MySqlErrorCode.NoReferencedRow2, (await database.Attempt("insert into orders (vendor_id) values (99)"))?.ErrorCode);
    }

    [Fact]
    public async Task Columns_the_server_fills_come_back_as_they_were()
    {
        using var database = await Migrated("reset_generated", Seeded);
        var created = await database.Column<DateTime>("select created_at from categories order by id");
        await database.Apply("update categories set name = 'Renamed' where id = 1");

        await database.Reset();

        Assert.Equal([6, 5], await database.Column<int>("select name_length from categories order by id"));
        Assert.Equal(created, await database.Column<DateTime>("select created_at from categories order by id"));
    }

    [Fact]
    public async Task Migrations_that_leave_no_rows_leave_nothing_aside()
    {
        using var database = await Migrated("reset_empty", "create table orders (id int auto_increment primary key)");
        await database.Apply("insert into orders values ()");

        await database.Reset();

        Assert.Equal(1, await database.Inserted("insert into orders values ()"));
        Assert.Equal([0L], await database.Column<long>(
            "select count(*) from information_schema.tables where table_schema = database() and table_name like 'specrig\\_kept%'"));
    }

    [Fact]
    public async Task A_kept_table_is_left_as_the_case_left_it()
    {
        using var database = await Migrated("reset_keep", Seeded, "states");
        await database.Apply("insert into states (name) values ('extra')");

        await database.Reset();

        Assert.Equal(["new", "active", "closed", "extra"], await database.Column<string>("select name from states order by id"));
        Assert.Equal(5, await database.Inserted("insert into states (name) values ('next')"));
    }

    [Fact]
    public async Task The_migration_history_is_left_as_it_is()
    {
        using var database = await Migrated("reset_history", """
            create table __EFMigrationsHistory (MigrationId varchar(150) primary key, ProductVersion varchar(32) not null);
            insert into __EFMigrationsHistory values ('20261023152955_Orders', '10.0.13');
            create table orders (id int auto_increment primary key);
            """);
        await database.Apply("insert into __EFMigrationsHistory values ('20261111120000_Later', '10.0.15')");
        await database.Apply("insert into orders values ()");

        await database.Reset();

        Assert.Equal(["20261023152955_Orders", "20261111120000_Later"],
            await database.Column<string>("select MigrationId from __EFMigrationsHistory order by MigrationId"));
        Assert.Empty(await database.Column<int>("select id from orders"));
    }

    [Fact]
    public async Task The_rows_a_context_seeds_come_back()
    {
        var (mysql, services) = await Booted("context_seed");
        using var _ = mysql;
        await using var __ = services;
        await Add(services, "closed");

        await mysql.Reset(CancellationToken.None);

        Assert.Equal(["new", "active"], await Names(services));
    }

    [Fact]
    public async Task A_refused_table_refuses_every_write_and_still_reads()
    {
        using var database = await Migrated("refuse_all", Seeded);

        await database.Mysql.Refuse("vendors");

        Assert.Equal(Refused("insert", "vendors"), (await database.Attempt("insert into vendors (state_id, name) values (1, 'R2')"))?.Message);
        Assert.Equal(Refused("update", "vendors"), (await database.Attempt("update vendors set name = 'renamed'"))?.Message);
        Assert.Equal(Refused("delete", "vendors"), (await database.Attempt("delete from vendors"))?.Message);
        Assert.Equal(["R1"], await database.Column<string>("select name from vendors"));
    }

    [Fact]
    public async Task A_refusal_leaves_the_other_tables_alone()
    {
        using var database = await Migrated("refuse_elsewhere", Seeded);

        await database.Mysql.Refuse("vendors");

        Assert.Null(await database.Attempt("insert into states (name) values ('extra')"));
    }

    [Fact]
    public async Task A_refusal_leaves_the_writes_it_does_not_name_alone()
    {
        using var database = await Migrated("refuse_named", Seeded);

        await database.Mysql.Refuse("vendors", Write.Delete);

        Assert.Null(await database.Attempt("update vendors set name = 'renamed'"));
        Assert.Equal(Refused("delete", "vendors"), (await database.Attempt("delete from vendors"))?.Message);
    }

    [Fact]
    public async Task A_table_that_is_not_there_is_named_in_the_failure()
    {
        using var database = await Migrated("refuse_missing", Seeded);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => database.Mysql.Refuse("nowhere"));

        Assert.Equal($"mysql: cannot refuse writes to nowhere: Table 'refuse_missing_test_{Environment.ProcessId}.nowhere' doesn't exist", refused.Message);
    }

    [Fact]
    public async Task A_table_named_up_to_the_identifier_limit_can_be_refused()
    {
        var table = new string('t', 64);
        using var database = await Migrated("refuse_long", $"create table {table} (id int auto_increment primary key)");

        await database.Mysql.Refuse(table);

        Assert.Equal(Refused("insert", table), (await database.Attempt($"insert into {table} values ()"))?.Message);
    }

    [Fact]
    public async Task A_quota_lets_that_many_rows_through_whichever_write_they_are()
    {
        using var database = await Migrated("refuse_quota", Seeded);

        await database.Mysql.RefuseAfter("states", 1);

        Assert.Null(await database.Attempt("update states set name = 'renamed' where id = 1"));
        Assert.Equal(Refused("insert", "states"), (await database.Attempt("insert into states (name) values ('second')"))?.Message);
    }

    [Fact]
    public async Task A_quota_counts_the_rows_of_a_statement()
    {
        using var database = await Migrated("refuse_rows", Seeded);
        await database.Mysql.RefuseAfter("states", 2, Write.Insert);

        var refused = await database.Attempt("insert into states (name) values ('first'), ('second'), ('third')");

        Assert.NotNull(refused);
        Assert.Equal(["new", "active", "closed"], await database.Column<string>("select name from states order by id"));
    }

    [Fact]
    public async Task A_rolled_back_row_gives_its_place_back()
    {
        using var database = await Migrated("refuse_rolled", Seeded);
        await database.Mysql.RefuseAfter("states", 1);

        await database.Rolled("insert into states (name) values ('undone')");

        Assert.Null(await database.Attempt("insert into states (name) values ('kept')"));
    }

    [Fact]
    public async Task Refusing_again_replaces_the_quota()
    {
        using var database = await Migrated("refuse_again", Seeded);
        await database.Mysql.Refuse("states");

        await database.Mysql.RefuseAfter("states", 1);

        Assert.Null(await database.Attempt("insert into states (name) values ('first')"));
    }

    [Fact]
    public async Task A_refusal_set_before_the_boot_waits_for_the_start()
    {
        using var mysql = new Mysql("ConnectionStrings:Shop");
        await mysql.Refuse("vendors", Write.Insert);
        using var database = new Database(mysql, await mysql.Provision("refuse_early", CancellationToken.None));
        await database.Apply(Seeded);
        await using var services = new ServiceCollection().BuildServiceProvider();
        await mysql.Migrate(services, CancellationToken.None);
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
        await database.Mysql.Refuse("vendors");

        await database.Reset();

        Assert.Null(await database.Attempt("insert into vendors (state_id, name) values (1, 'R2')"));
        Assert.Equal([0L], await database.Column<long>(
            "select count(*) from information_schema.triggers where trigger_schema = database() and trigger_name like 'specrig%'"));
        Assert.Equal([0L], await database.Column<long>(
            "select count(*) from information_schema.tables where table_schema = database() and table_name = 'specrig_refusal'"));
    }

    [Fact]
    public async Task A_fault_set_before_the_boot_waits_for_the_start()
    {
        using var mysql = new Mysql("ConnectionStrings:Shop");
        mysql.FailWith(null);
        using var database = new Database(mysql, await mysql.Provision("fail_early", CancellationToken.None));
        await database.Apply(Seeded);
        await using var services = new ServiceCollection().BuildServiceProvider();
        await mysql.Migrate(services, CancellationToken.None);
        await database.Reset();
        Assert.Equal(["R1"], await database.Column<string>("select name from vendors"));

        await database.Arm();

        Assert.Equal(MySqlErrorCode.UnableToConnectToHost, (await Assert.ThrowsAsync<MySqlException>(() => database.Apply("select 1"))).ErrorCode);
    }

    [Fact]
    public async Task A_context_meets_a_database_that_refuses_connections_until_the_heal_and_the_rows_stay()
    {
        var (mysql, services) = await Booted("fail_heal_context");
        using var _ = mysql;
        await using var __ = services;
        await mysql.Arm(CancellationToken.None);
        await Add(services, "closed");

        mysql.FailWith(null);
        var transient = await Assert.ThrowsAsync<InvalidOperationException>(() => Names(services));
        Assert.Equal(MySqlErrorCode.UnableToConnectToHost, Assert.IsType<MySqlException>(transient.InnerException).ErrorCode);

        await mysql.Heal();

        Assert.Equal(["new", "active", "closed"], await Names(services));
    }

    [Fact]
    public async Task A_context_meets_the_error_a_step_chose()
    {
        var (mysql, services) = await Booted("fail_chosen");
        using var _ = mysql;
        await using var __ = services;
        await mysql.Arm(CancellationToken.None);

        mysql.FailWith(new InvalidOperationException("the disk is full"));

        Assert.Equal("the disk is full", (await Assert.ThrowsAsync<InvalidOperationException>(() => Names(services))).Message);
    }

    [Fact]
    public async Task A_heal_lifts_the_refusals_and_the_fault_and_keeps_the_rows()
    {
        using var database = await Migrated("refuse_heal", Seeded);
        await database.Arm();
        await database.Apply("insert into states (name) values ('extra')");
        await database.Mysql.Refuse("vendors");
        database.Mysql.FailWith(null);
        await Assert.ThrowsAsync<MySqlException>(() => database.Apply("select 1"));

        await database.Mysql.Heal();

        Assert.Null(await database.Attempt("insert into vendors (state_id, name) values (1, 'R2')"));
        Assert.Equal(["new", "active", "closed", "extra"], await database.Column<string>("select name from states order by id"));
    }

    [Fact]
    public async Task A_heal_before_the_boot_drops_what_was_waiting_for_it()
    {
        using var mysql = new Mysql("ConnectionStrings:Shop");
        await mysql.Refuse("vendors");
        mysql.FailWith(null);
        using var database = new Database(mysql, await mysql.Provision("refuse_heal_early", CancellationToken.None));
        await database.Apply(Seeded);
        await using var services = new ServiceCollection().BuildServiceProvider();
        await mysql.Migrate(services, CancellationToken.None);

        await mysql.Heal();
        await mysql.Reset(CancellationToken.None);
        await mysql.Arm(CancellationToken.None);

        Assert.Null(await database.Attempt("insert into vendors (state_id, name) values (1, 'R2')"));
        Assert.Equal(["R1", "R2"], await database.Column<string>("select name from vendors order by id"));
    }

    [Fact]
    public async Task A_run_sweeps_what_a_killed_run_left_and_nothing_else()
    {
        var admin = new MySqlConnectionStringBuilder(await new Mysql("ConnectionStrings:Sweep").Provision("sweep", CancellationToken.None)) { Database = "" };
        await using var connection = new MySqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        async Task Execute(string sql)
        {
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        var twoHoursAgo = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
        foreach (var (name, mark) in new[] { ("sweep_test_1", $"specrig {twoHoursAgo}"), ("sweep_test_2", $"specrig {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"), ("sweep_test_kept", $"specrig {twoHoursAgo}"), ("sweep_test_3", "kept by hand") })
        {
            await Execute($"DROP DATABASE IF EXISTS `{name}`");
            await Execute($"CREATE DATABASE `{name}`");
            await Execute($"CREATE TABLE `{name}`.specrig_mark (id int) COMMENT '{mark}'");
        }
        await Execute("DROP DATABASE IF EXISTS `sweep_test_4`");
        await Execute("CREATE DATABASE `sweep_test_4`");

        await MySqlServer.Start("sweep", admin.ConnectionString, "unused");

        var left = new List<string>();
        await using (var list = new MySqlCommand("SELECT schema_name FROM information_schema.schemata WHERE schema_name LIKE 'sweep\\_test\\_%'", connection))
        await using (var reader = await list.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                left.Add(reader.GetString(0));
            }
        }
        Assert.DoesNotContain("sweep_test_1", left);
        Assert.Contains("sweep_test_2", left);
        Assert.Contains("sweep_test_kept", left);
        Assert.Contains("sweep_test_3", left);
        Assert.Contains("sweep_test_4", left);
        foreach (var name in new[] { "sweep_test_2", "sweep_test_kept", "sweep_test_3", "sweep_test_4" })
        {
            await Execute($"DROP DATABASE IF EXISTS `{name}`");
        }
    }

    [Fact]
    public async Task The_end_of_the_run_drops_the_database_a_transaction_still_holds()
    {
        var admin = new MySqlConnectionStringBuilder(await new Mysql("ConnectionStrings:Held").Provision("held_first", CancellationToken.None)) { Database = "" };
        var server = await MySqlServer.Start("held", admin.ConnectionString, "unused");
        await using var holding = new MySqlConnection(server.Address);
        await holding.OpenAsync();
        foreach (var sql in new[] { "create table orders (id int)", "start transaction", "select * from orders" })
        {
            await using var command = new MySqlCommand(sql, holding);
            await command.ExecuteNonQueryAsync();
        }

        await server.Down().WaitAsync(TimeSpan.FromSeconds(30));

        await using var connection = new MySqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var exists = new MySqlCommand($"SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'held_test_{Environment.ProcessId}'", connection);
        Assert.Equal(0L, await exists.ExecuteScalarAsync());
    }

    [Fact]
    public async Task An_address_that_leads_nowhere_fails_the_boot_without_its_password()
    {
        using var nowhere = new Mysql("ConnectionStrings:Shop") { Address = "Server=nowhere.invalid;Port=3306;User ID=shop;Password=s3cret;Connection Timeout=2" };

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => nowhere.Provision("nowhere", CancellationToken.None));

        Assert.StartsWith("mysql at nowhere.invalid:3306:", refused.Message);
        Assert.DoesNotContain("s3cret", refused.ToString());
    }
}
