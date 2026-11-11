using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Builders;
using MySqlConnector;

namespace Specrig;

sealed class MySqlServer(string maintenance, string database, string address) : Server(maintenance, database, address)
{
    const string Marked = "specrig_mark";
    const string Refusals = "specrig_refusal";

    // The checks switched off and a trigger's sql_mode stay with the session, which the pool resets before it hands it out again.
    readonly string own = new MySqlConnectionStringBuilder(address) { ConnectionReset = true, IgnoreCommandTransaction = true }.ConnectionString;
    Baseline? baseline;

    public static async Task<MySqlServer> Start(string service, string? given, string image)
    {
        var admin = new MySqlConnectionStringBuilder(given ?? await Container(service, image));
        var stem = admin.Database is "" ? $"{service}_test" : admin.Database;
        var database = $"{stem}_{Environment.ProcessId}";
        admin.Database = "";
        var server = new MySqlServer(admin.ConnectionString, database, new MySqlConnectionStringBuilder(admin.ConnectionString) { Database = database }.ConnectionString);
        await server.Open(stem, $"mysql at {admin.Server}:{admin.Port}");
        return server;
    }

    static async Task<string> Container(string service, string image)
    {
        var container = new ContainerBuilder(image)
            .WithLabel("specrig", "mysql")
            .WithEnvironment("MYSQL_ALLOW_EMPTY_PASSWORD", "yes")
            .WithPortBinding(3306, true)
            // With no password, a port on every interface is an open database on whatever network the machine is on.
            .WithCreateParameterModifier(parameters =>
            {
                foreach (var binding in parameters.HostConfig!.PortBindings.Values.SelectMany(bindings => bindings))
                {
                    binding.HostIP = "127.0.0.1";
                }
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(3306))
            .WithReuse(true)
            .Build();
        try
        {
            await container.StartAsync();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"mysql: no address is given and no container started — set {service.ToUpperInvariant()}_TEST_MYSQL, or start Docker: {e.Message}", e);
        }
        return new MySqlConnectionStringBuilder { Server = container.Hostname, Port = container.GetMappedPublicPort(3306), UserID = "root" }.ConnectionString;
    }

    public override async Task Discover(IReadOnlyList<string> keep, CancellationToken cancel)
    {
        await using var connection = await Connected(cancel);
        var tables = (await connection.Rows(
                """
                SELECT table_name FROM information_schema.tables
                WHERE table_schema = database() AND table_type = 'BASE TABLE' AND table_name NOT LIKE 'specrig\_%'
                """,
                row => row.GetString(0), cancel))
            .Where(table => table != "__EFMigrationsHistory" && !keep.Contains(table)).ToList();
        var inserts = new Dictionary<string, string>();
        foreach (var table in tables)
        {
            if (!(await connection.Rows($"SELECT EXISTS (SELECT 1 FROM {Quoted(table)})", row => row.GetBoolean(0), cancel)).Single())
            {
                continue;
            }
            var columns = string.Join(", ", await connection.Rows(
                """
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = database() AND table_name = ? AND generation_expression = ''
                ORDER BY ordinal_position
                """,
                row => Quoted(row.GetString(0)), cancel, table));
            var copy = $"specrig_kept_{inserts.Count}";
            await connection.Execute($"CREATE TABLE {copy} AS SELECT {columns} FROM {Quoted(table)}", cancel);
            inserts[table] = $"INSERT INTO {Quoted(table)} ({columns}) SELECT {columns} FROM {copy}";
        }
        var triggers = new List<Trigger>();
        var found = await connection.Rows(
            """
            SELECT trigger_name, event_object_table, event_manipulation FROM information_schema.triggers
            WHERE trigger_schema = database() AND event_manipulation IN ('INSERT', 'DELETE')
            ORDER BY event_object_table, action_order
            """,
            row => (Name: row.GetString(0), Table: row.GetString(1), Event: row.GetString(2)), cancel);
        foreach (var (name, table, @event) in found.Where(trigger => trigger.Event == "DELETE" ? tables.Contains(trigger.Table) : inserts.ContainsKey(trigger.Table)))
        {
            var (mode, statement) = (await connection.Rows($"SHOW CREATE TRIGGER {Quoted(name)}", row => (row.GetString(1), row.GetString(2)), cancel)).Single();
            triggers.Add(new Trigger(name, table, @event, mode, statement));
        }
        baseline = new Baseline(tables, inserts, await Checksums(connection, tables, cancel), await Increments(connection, tables, cancel), triggers);
        await Reset(cancel);
    }

    // Truncating, a trigger and a counter are DDL here, milliseconds each, so only what a case changed is touched.
    public override async Task Reset(CancellationToken cancel)
    {
        var (tables, inserts, checksums, increments, triggers) = baseline ?? throw new InvalidOperationException("mysql is reset before it is migrated");
        await using var connection = await Connected(cancel);
        var present = await Triggers(connection, cancel);
        await Lift(connection, present, cancel);
        var now = await Checksums(connection, tables, cancel);
        var changed = tables.Where(table => now[table] != checksums[table]).ToList();
        var redone = triggers
            .Where(trigger => !present.Contains(trigger.Name) || changed.Contains(trigger.Table) && (trigger.Event == "INSERT" || now[trigger.Table] != 0))
            .Select(trigger => (trigger.Table, trigger.Event)).ToHashSet();
        var firing = triggers.Where(trigger => redone.Contains((trigger.Table, trigger.Event))).ToList();
        foreach (var trigger in firing.Where(trigger => present.Contains(trigger.Name)))
        {
            await connection.Execute($"DROP TRIGGER {Quoted(trigger.Name)}", cancel);
        }
        if (changed.Count > 0)
        {
            await using var refill = await connection.BeginTransactionAsync(cancel);
            foreach (var table in changed)
            {
                await connection.Execute($"DELETE FROM {Quoted(table)}", cancel);
            }
            foreach (var table in changed.Where(inserts.ContainsKey))
            {
                await connection.Execute(inserts[table], cancel);
            }
            await refill.CommitAsync(cancel);
        }
        var current = await Increments(connection, tables, cancel);
        foreach (var (table, next) in increments.Where(kept => current.GetValueOrDefault(kept.Key) != kept.Value))
        {
            await connection.Execute($"ALTER TABLE {Quoted(table)} AUTO_INCREMENT = {next}", cancel);
        }
        foreach (var trigger in firing)
        {
            await connection.Execute($"SET SESSION sql_mode = {Literal(trigger.Mode)}", cancel);
            await connection.Execute(trigger.Statement, cancel);
        }
    }

    public override async Task Refuse(Refusal refusal, CancellationToken cancel)
    {
        var writes = refusal.Writes.Select(write => write.ToString().ToLowerInvariant()).ToList();
        var name = $"specrig_refuse_{string.Join('_', writes)}";
        var quota = $"relation = {Literal(refusal.Table)} AND name = {Literal(name)}";
        try
        {
            await using var connection = await Connected(cancel);
            await connection.Execute(
                $"CREATE TABLE IF NOT EXISTS {Refusals} (relation varchar(64) NOT NULL, name varchar(64) NOT NULL, remaining int NOT NULL, PRIMARY KEY (relation, name))",
                cancel);
            await connection.Execute(
                $"INSERT INTO {Refusals} (relation, name, remaining) VALUES (?, ?, ?) AS kept ON DUPLICATE KEY UPDATE remaining = kept.remaining",
                cancel, refusal.Table, name, refusal.Rows);
            foreach (var write in writes)
            {
                var trigger = Quoted(Triggered(name, write, refusal.Table));
                await connection.Execute($"DROP TRIGGER IF EXISTS {trigger}", cancel);
                await connection.Execute(
                    $"""
                    CREATE TRIGGER {trigger} BEFORE {write} ON {Quoted(refusal.Table)} FOR EACH ROW BEGIN
                        UPDATE {Refusals} SET remaining = remaining - 1 WHERE {quota};
                        IF (SELECT remaining FROM {Refusals} WHERE {quota}) < 0 THEN
                            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = {Literal($"specrig: {write} on {refusal.Table} is refused")};
                        END IF;
                    END
                    """,
                    cancel);
            }
        }
        catch (MySqlException e)
        {
            throw new InvalidOperationException($"mysql: cannot refuse writes to {refusal.Table}: {e.Message}", e);
        }
    }

    public override async Task Lift(CancellationToken cancel)
    {
        await using var connection = await Connected(cancel);
        await Lift(connection, await Triggers(connection, cancel), cancel);
    }

    protected override DbConnection Connect(string address) => new MySqlConnection(address);

    protected override Task<List<(string Name, string Mark, long Connected)>> Databases(DbConnection admin) => admin.Rows(
        $"""
        SELECT s.schema_name, coalesce(t.table_comment, ''),
               (SELECT count(*) FROM information_schema.processlist p WHERE p.db = s.schema_name)
        FROM information_schema.schemata s
        LEFT JOIN information_schema.tables t ON t.table_schema = s.schema_name AND t.table_name = '{Marked}'
        """,
        row => (row.GetString(0), row.GetString(1), row.GetInt64(2)));

    protected override async Task Create(DbConnection admin, string name, string mark)
    {
        await admin.Execute($"CREATE DATABASE {Quoted(name)}");
        await admin.Execute($"CREATE TABLE {Quoted(name)}.{Marked} (id int) COMMENT {Literal(mark)}");
    }

    protected override async Task Drop(DbConnection admin, string name)
    {
        foreach (var id in await admin.Rows("SELECT id FROM information_schema.processlist WHERE db = ?", row => row.GetValue(0), default, name))
        {
            try
            {
                await admin.Execute($"KILL {id}");
            }
            catch (MySqlException e) when (e.ErrorCode == MySqlErrorCode.NoSuchThread)
            {
            }
        }
        await admin.Execute($"DROP DATABASE IF EXISTS {Quoted(name)}");
    }

    async Task<MySqlConnection> Connected(CancellationToken cancel)
    {
        var connection = new MySqlConnection(own);
        await connection.OpenAsync(cancel);
        await connection.Execute(
            "SET SESSION foreign_key_checks = 0, SESSION information_schema_stats_expiry = 0, SESSION sql_mode = concat(@@sql_mode, ',NO_AUTO_VALUE_ON_ZERO')",
            cancel);
        return connection;
    }

    static async Task<HashSet<string>> Triggers(DbConnection connection, CancellationToken cancel) =>
        [.. await connection.Rows("SELECT trigger_name FROM information_schema.triggers WHERE trigger_schema = database()", row => row.GetString(0), cancel)];

    static async Task Lift(DbConnection connection, IEnumerable<string> triggers, CancellationToken cancel)
    {
        foreach (var trigger in triggers.Where(trigger => trigger.StartsWith("specrig_refuse_", StringComparison.Ordinal)))
        {
            await connection.Execute($"DROP TRIGGER {Quoted(trigger)}", cancel);
        }
        // Asked first: a drop is DDL even when there is nothing to drop.
        var quotas = await connection.Rows(
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = database() AND table_name = '{Refusals}'",
            row => row.GetInt64(0), cancel);
        if (quotas.Single() > 0)
        {
            await connection.Execute($"DROP TABLE {Refusals}", cancel);
        }
    }

    static async Task<Dictionary<string, ulong>> Checksums(DbConnection connection, IReadOnlyList<string> tables, CancellationToken cancel)
    {
        if (tables.Count == 0)
        {
            return [];
        }
        var sums = await connection.Rows($"CHECKSUM TABLE {string.Join(", ", tables.Select(Quoted))}",
            row => row.IsDBNull(1) ? 0 : Convert.ToUInt64(row.GetValue(1)), cancel);
        return tables.Zip(sums).ToDictionary(pair => pair.First, pair => pair.Second);
    }

    static async Task<Dictionary<string, ulong>> Increments(DbConnection connection, IReadOnlyList<string> tables, CancellationToken cancel)
    {
        var found = await connection.Rows(
            "SELECT table_name, auto_increment FROM information_schema.tables WHERE table_schema = database() AND auto_increment IS NOT NULL",
            row => (Table: row.GetString(0), Next: Convert.ToUInt64(row.GetValue(1))), cancel);
        return found.Where(row => tables.Contains(row.Table)).ToDictionary(row => row.Table, row => row.Next);
    }

    static string Triggered(string refusal, string write, string table) =>
        $"specrig_refuse_{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{refusal} {write} {table}")))[..16]}";

    static string Quoted(string name) => $"`{name.Replace("`", "``")}`";

    static string Literal(string value) => $"'{value.Replace("\\", "\\\\").Replace("'", "''")}'";

    sealed record Baseline(
        IReadOnlyList<string> Tables,
        IReadOnlyDictionary<string, string> Inserts,
        IReadOnlyDictionary<string, ulong> Checksums,
        IReadOnlyDictionary<string, ulong> Increments,
        IReadOnlyList<Trigger> Triggers);

    sealed record Trigger(string Name, string Table, string Event, string Mode, string Statement);
}
