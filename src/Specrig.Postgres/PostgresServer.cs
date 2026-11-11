using System.Data.Common;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Specrig;

sealed class PostgresServer(string maintenance, string database, string address) : Server(maintenance, database, address)
{
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

    Baseline? baseline;

    public static async Task<PostgresServer> Start(string service, string? given, string image)
    {
        var admin = new NpgsqlConnectionStringBuilder(given ?? await Container(service, image));
        var stem = admin.Database is null or "postgres" ? $"{service}_test" : admin.Database;
        var database = $"{stem}_{Environment.ProcessId}";
        admin.Database = "postgres";
        var server = new PostgresServer(admin.ConnectionString, database, new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = database }.ConnectionString);
        await server.Open(stem, $"postgres at {admin.Host}:{admin.Port}");
        return server;
    }

    static async Task<string> Container(string service, string image)
    {
        var container = new PostgreSqlBuilder(image)
            .WithLabel("specrig", "postgres")
            .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "trust")
            // With trust, a port on every interface is an open database on whatever network the machine is on.
            .WithCreateParameterModifier(parameters =>
            {
                foreach (var binding in parameters.HostConfig!.PortBindings.Values.SelectMany(bindings => bindings))
                {
                    binding.HostIP = "127.0.0.1";
                }
            })
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

    public override async Task Discover(IReadOnlyList<string> keep, CancellationToken cancel)
    {
        await using var connection = new NpgsqlConnection(Address);
        await connection.OpenAsync(cancel);
        var found = await connection.Rows(
            $"SELECT schemaname, tablename FROM pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema', '{Copies}', '{Refusals}')",
            row => (Schema: row.GetString(0), Name: row.GetString(1)), cancel);
        await connection.Execute($"DROP SCHEMA IF EXISTS {Copies} CASCADE", cancel);
        var tables = new List<string>();
        var restores = new List<string>();
        var sequences = new List<Sequence>();
        foreach (var (schema, name) in found.Where(table => table.Name != "__EFMigrationsHistory" && !keep.Contains(table.Name)))
        {
            var table = $"{Quoted(schema)}.{Quoted(name)}";
            tables.Add(table);
            if ((await connection.Rows($"SELECT NOT relispartition AND EXISTS (SELECT 1 FROM {table}) FROM pg_class WHERE oid = $1::regclass",
                    row => row.GetBoolean(0), cancel, table)).Single())
            {
                var columns = string.Join(", ", await connection.Rows(
                    "SELECT column_name FROM information_schema.columns WHERE table_schema = $1 AND table_name = $2 AND is_generated = 'NEVER' ORDER BY ordinal_position",
                    row => Quoted(row.GetString(0)), cancel, schema, name));
                var copy = $"{Copies}.t{restores.Count}";
                await connection.Execute($"CREATE SCHEMA IF NOT EXISTS {Copies}", cancel);
                await connection.Execute($"CREATE TABLE {copy} AS SELECT {columns} FROM {table}", cancel);
                restores.Add($"INSERT INTO {table} ({columns}) OVERRIDING SYSTEM VALUE SELECT {columns} FROM {copy}");
            }
            var owned = await connection.Rows(
                """
                SELECT owned FROM (
                    SELECT pg_get_serial_sequence(format('%I.%I', table_schema, table_name), column_name) AS owned
                    FROM information_schema.columns WHERE table_schema = $1 AND table_name = $2
                ) AS columns WHERE owned IS NOT NULL
                """,
                row => row.GetString(0), cancel, schema, name);
            foreach (var sequence in owned)
            {
                sequences.Add((await connection.Rows($"SELECT last_value, is_called FROM {sequence}",
                    row => new Sequence(sequence, row.GetInt64(0), row.GetBoolean(1)), cancel)).Single());
            }
        }
        baseline = new Baseline(tables, restores, sequences);
        await Reset(cancel);
    }

    public override async Task Reset(CancellationToken cancel)
    {
        var (tables, restores, sequences) = baseline ?? throw new InvalidOperationException("postgres is reset before it is migrated");
        await using var connection = new NpgsqlConnection(Address);
        await connection.OpenAsync(cancel);
        await connection.Execute(Lifting, cancel);
        await using var transaction = await connection.BeginTransactionAsync(cancel);
        if (restores.Count > 0)
        {
            try
            {
                await connection.Execute("SET LOCAL session_replication_role = replica", cancel);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                throw new InvalidOperationException($"postgres: putting back the rows the migrations left needs a superuser: {e.MessageText}", e);
            }
        }
        if (tables.Count > 0)
        {
            await connection.Execute($"TRUNCATE {string.Join(", ", tables)} RESTART IDENTITY CASCADE", cancel);
        }
        foreach (var restore in restores)
        {
            await connection.Execute(restore, cancel);
        }
        if (sequences.Count > 0)
        {
            await connection.Execute(
                "SELECT setval(name::regclass, value, called) FROM unnest($1::text[], $2::bigint[], $3::bool[]) AS kept(name, value, called)", cancel,
                sequences.Select(s => s.Name).ToArray(), sequences.Select(s => s.Value).ToArray(), sequences.Select(s => s.Called).ToArray());
        }
        await transaction.CommitAsync(cancel);
    }

    public override async Task Refuse(Refusal refusal, CancellationToken cancel)
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
            await connection.Execute(RefusalSetup, cancel);
            await connection.Execute(
                $"INSERT INTO {Refusals}.quota (relation, name, remaining) VALUES ($1::regclass, $2, $3) ON CONFLICT (relation, name) DO UPDATE SET remaining = excluded.remaining",
                cancel, table, name, refusal.Rows);
            await connection.Execute($"DROP TRIGGER IF EXISTS {name} ON {table}", cancel);
            await connection.Execute($"{trigger} EXECUTE FUNCTION {Refusals}.refuse()", cancel);
            await transaction.CommitAsync(cancel);
        }
        catch (PostgresException e)
        {
            throw new InvalidOperationException($"postgres: cannot refuse writes to {refusal.Table}: {e.MessageText}", e);
        }
    }

    public override async Task Lift(CancellationToken cancel)
    {
        await using var connection = new NpgsqlConnection(Address);
        await connection.OpenAsync(cancel);
        await connection.Execute(Lifting, cancel);
    }

    public override void Forget() => NpgsqlConnection.ClearAllPools();

    protected override DbConnection Connect(string address) => new NpgsqlConnection(address);

    protected override Task<List<(string Name, string Mark, long Connected)>> Databases(DbConnection admin) => admin.Rows(
        """
        SELECT d.datname,
               coalesce(shobj_description(d.oid, 'pg_database'), ''),
               (SELECT count(*) FROM pg_stat_activity a WHERE a.datname = d.datname)
        FROM pg_database d
        """,
        row => (row.GetString(0), row.GetString(1), row.GetInt64(2)));

    protected override async Task Create(DbConnection admin, string name, string mark)
    {
        await admin.Execute($"CREATE DATABASE {Quoted(name)}");
        await admin.Execute($"COMMENT ON DATABASE {Quoted(name)} IS '{mark}'");
    }

    protected override Task Drop(DbConnection admin, string name) => admin.Execute($"DROP DATABASE IF EXISTS {Quoted(name)} WITH (FORCE)");

    static string Quoted(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    static string Relation(string table) => string.Join('.', table.Split('.', 2).Select(Quoted));

    sealed record Baseline(IReadOnlyList<string> Tables, IReadOnlyList<string> Restores, IReadOnlyList<Sequence> Sequences);

    sealed record Sequence(string Name, long Value, bool Called);
}
