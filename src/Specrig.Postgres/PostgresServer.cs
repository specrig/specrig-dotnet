using System.Text.RegularExpressions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Specrig;

sealed class PostgresServer
{
    const string Mark = "specrig ";
    const string Copies = "specrig_baseline";
    const string Refusals = "specrig_refusal";

    const string Lifting = $"DROP SCHEMA IF EXISTS {Refusals} CASCADE";

    const string RefusalSetup = $$"""
        CREATE SCHEMA IF NOT EXISTS {{Refusals}};
        CREATE TABLE IF NOT EXISTS {{Refusals}}.quota (relation oid, name text, remaining int NOT NULL, PRIMARY KEY (relation, name));
        CREATE OR REPLACE FUNCTION {{Refusals}}.refuse() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            rest int;
        BEGIN
            UPDATE {{Refusals}}.quota SET remaining = remaining - 1 WHERE relation = tg_relid AND name = tg_name RETURNING remaining INTO rest;
            IF rest < 0 THEN
                RAISE EXCEPTION 'specrig: % on % is refused', lower(tg_op), tg_table_name;
            END IF;
            IF tg_op = 'DELETE' THEN
                RETURN old;
            END IF;
            RETURN new;
        END $$;
        """;

    readonly Lock gate = new();
    readonly string maintenance;
    readonly string database;
    Task? migration;
    Baseline? baseline;

    PostgresServer(string maintenance, string database, string address)
    {
        this.maintenance = maintenance;
        this.database = database;
        Address = address;
    }

    public string Address { get; }

    public static async Task<PostgresServer> Start(string service, string? given, string image)
    {
        var admin = new NpgsqlConnectionStringBuilder(given ?? await Container(service, image));
        var stem = admin.Database is null or "postgres" ? $"{service}_test" : admin.Database;
        var database = $"{stem}_{Environment.ProcessId}";
        admin.Database = "postgres";
        try
        {
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await Sweep(connection, stem);
            await Execute(connection, $"DROP DATABASE IF EXISTS {Quoted(database)} WITH (FORCE)");
            await Execute(connection, $"CREATE DATABASE {Quoted(database)}");
            await Execute(connection, $"COMMENT ON DATABASE {Quoted(database)} IS '{Mark}{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}'");
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException)
        {
            throw new InvalidOperationException($"postgres at {admin.Host}:{admin.Port}: {e.Message}", e);
        }
        var server = new PostgresServer(admin.ConnectionString, database, new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = database }.ConnectionString);
        Rig.Ending(server.Down);
        return server;
    }

    static async Task<string> Container(string service, string image)
    {
        var container = new PostgreSqlBuilder(image)
            .WithLabel("specrig", "postgres")
            .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "trust")
            .WithReuse(true)
            .Build();
        try
        {
            await container.StartAsync();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"postgres: no address is given and no container started — set {service.ToUpperInvariant()}_TEST_POSTGRES, or start Docker: {e.Message}", e);
        }
        return container.GetConnectionString();
    }

    public Task Once(Func<Task> migrate)
    {
        lock (gate)
        {
            return migration ??= migrate();
        }
    }

    public async Task Discover(IReadOnlyList<string> keep, CancellationToken cancel)
    {
        await using var connection = new NpgsqlConnection(Address);
        await connection.OpenAsync(cancel);
        var found = await Rows(connection,
            $"SELECT schemaname, tablename FROM pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema', '{Copies}', '{Refusals}')",
            row => (Schema: row.GetString(0), Name: row.GetString(1)), cancel);
        await Execute(connection, $"DROP SCHEMA IF EXISTS {Copies} CASCADE", cancel);
        var tables = new List<string>();
        var restores = new List<string>();
        var sequences = new List<Sequence>();
        foreach (var (schema, name) in found.Where(table => table.Name != "__EFMigrationsHistory" && !keep.Contains(table.Name)))
        {
            var table = $"{Quoted(schema)}.{Quoted(name)}";
            tables.Add(table);
            if ((await Rows(connection, $"SELECT NOT relispartition AND EXISTS (SELECT 1 FROM {table}) FROM pg_class WHERE oid = $1::regclass",
                    row => row.GetBoolean(0), cancel, table)).Single())
            {
                var columns = string.Join(", ", await Rows(connection,
                    "SELECT column_name FROM information_schema.columns WHERE table_schema = $1 AND table_name = $2 AND is_generated = 'NEVER' ORDER BY ordinal_position",
                    row => Quoted(row.GetString(0)), cancel, schema, name));
                var copy = $"{Copies}.t{restores.Count}";
                await Execute(connection, $"CREATE SCHEMA IF NOT EXISTS {Copies}", cancel);
                await Execute(connection, $"CREATE TABLE {copy} AS SELECT {columns} FROM {table}", cancel);
                restores.Add($"INSERT INTO {table} ({columns}) OVERRIDING SYSTEM VALUE SELECT {columns} FROM {copy}");
            }
            var owned = await Rows(connection,
                """
                SELECT owned FROM (
                    SELECT pg_get_serial_sequence(format('%I.%I', table_schema, table_name), column_name) AS owned
                    FROM information_schema.columns WHERE table_schema = $1 AND table_name = $2
                ) AS columns WHERE owned IS NOT NULL
                """,
                row => row.GetString(0), cancel, schema, name);
            foreach (var sequence in owned)
            {
                sequences.Add((await Rows(connection, $"SELECT last_value, is_called FROM {sequence}",
                    row => new Sequence(sequence, row.GetInt64(0), row.GetBoolean(1)), cancel)).Single());
            }
        }
        baseline = new Baseline(tables, restores, sequences);
        await Reset(cancel);
    }

    public async Task Reset(CancellationToken cancel)
    {
        var (tables, restores, sequences) = baseline ?? throw new InvalidOperationException("postgres is reset before it is migrated");
        await using var connection = new NpgsqlConnection(Address);
        await connection.OpenAsync(cancel);
        await Execute(connection, Lifting, cancel);
        await using var transaction = await connection.BeginTransactionAsync(cancel);
        if (restores.Count > 0)
        {
            try
            {
                await Execute(connection, "SET LOCAL session_replication_role = replica", cancel);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                throw new InvalidOperationException($"postgres: putting back the rows the migrations left needs a superuser: {e.MessageText}", e);
            }
        }
        if (tables.Count > 0)
        {
            await Execute(connection, $"TRUNCATE {string.Join(", ", tables)} RESTART IDENTITY CASCADE", cancel);
        }
        foreach (var restore in restores)
        {
            await Execute(connection, restore, cancel);
        }
        if (sequences.Count > 0)
        {
            await Execute(connection,
                "SELECT setval(name::regclass, value, called) FROM unnest($1::text[], $2::bigint[], $3::bool[]) AS kept(name, value, called)", cancel,
                sequences.Select(s => s.Name).ToArray(), sequences.Select(s => s.Value).ToArray(), sequences.Select(s => s.Called).ToArray());
        }
        await transaction.CommitAsync(cancel);
    }

    public async Task Refuse(Refusal refusal, CancellationToken cancel)
    {
        var table = Relation(refusal.Table);
        var writes = refusal.Writes.Select(write => write.ToString().ToLowerInvariant()).ToList();
        var name = $"specrig_refuse_{(refusal.Commit ? "commit_" : "")}{string.Join('_', writes)}";
        var events = string.Join(" OR ", writes);
        var trigger = refusal.Commit
            ? $"CREATE CONSTRAINT TRIGGER {name} AFTER {events} ON {table} DEFERRABLE INITIALLY DEFERRED FOR EACH ROW"
            : $"CREATE TRIGGER {name} BEFORE {events} ON {table} FOR EACH ROW";
        try
        {
            await using var connection = new NpgsqlConnection(Address);
            await connection.OpenAsync(cancel);
            await using var transaction = await connection.BeginTransactionAsync(cancel);
            await Execute(connection, RefusalSetup, cancel);
            await Execute(connection,
                $"INSERT INTO {Refusals}.quota (relation, name, remaining) VALUES ($1::regclass, $2, $3) ON CONFLICT (relation, name) DO UPDATE SET remaining = excluded.remaining",
                cancel, table, name, refusal.Rows);
            await Execute(connection, $"DROP TRIGGER IF EXISTS {name} ON {table}", cancel);
            await Execute(connection, $"{trigger} EXECUTE FUNCTION {Refusals}.refuse()", cancel);
            await transaction.CommitAsync(cancel);
        }
        catch (PostgresException e)
        {
            throw new InvalidOperationException($"postgres: cannot refuse writes to {refusal.Table}: {e.MessageText}", e);
        }
    }

    public async Task Lift(CancellationToken cancel)
    {
        await using var connection = new NpgsqlConnection(Address);
        await connection.OpenAsync(cancel);
        await Execute(connection, Lifting, cancel);
    }

    async Task Down()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(maintenance);
        await connection.OpenAsync();
        await Execute(connection, $"DROP DATABASE IF EXISTS {Quoted(database)} WITH (FORCE)");
    }

    static async Task Sweep(NpgsqlConnection connection, string stem)
    {
        var ours = new Regex($"^{Regex.Escape(stem)}_[0-9]+$");
        var stale = new List<string>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT d.datname,
                   coalesce(shobj_description(d.oid, 'pg_database'), ''),
                   (SELECT count(*) FROM pg_stat_activity a WHERE a.datname = d.datname)
            FROM pg_database d
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            var hourAgo = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                var mark = reader.GetString(1);
                if (ours.IsMatch(name) && reader.GetInt64(2) == 0
                    && mark.StartsWith(Mark) && long.TryParse(mark[Mark.Length..], out var at) && at < hourAgo)
                {
                    stale.Add(name);
                }
            }
        }
        foreach (var name in stale)
        {
            await Execute(connection, $"DROP DATABASE IF EXISTS {Quoted(name)} WITH (FORCE)");
        }
    }

    static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken cancel = default, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(values.Select(value => new NpgsqlParameter { Value = value }).ToArray());
        await command.ExecuteNonQueryAsync(cancel);
    }

    static async Task<List<T>> Rows<T>(NpgsqlConnection connection, string sql, Func<NpgsqlDataReader, T> row, CancellationToken cancel, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(values.Select(value => new NpgsqlParameter { Value = value }).ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancel);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancel))
        {
            rows.Add(row(reader));
        }
        return rows;
    }

    static string Quoted(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    static string Relation(string table) => string.Join('.', table.Split('.', 2).Select(Quoted));

    sealed record Baseline(IReadOnlyList<string> Tables, IReadOnlyList<string> Restores, IReadOnlyList<Sequence> Sequences);

    sealed record Sequence(string Name, long Value, bool Called);
}
