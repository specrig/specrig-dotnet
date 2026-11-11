using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Specrig;

sealed class SqliteFile : Store
{
    const string Copies = "specrig_baseline";
    const string Refusals = "specrig_refusal";

    readonly string dir;
    readonly string own;
    Baseline? baseline;

    SqliteFile(string dir)
    {
        this.dir = dir;
        // The bundled SQLite checks foreign keys by default, a system one does not.
        Address = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "test.db"), ForeignKeys = true }.ConnectionString;
        own = new SqliteConnectionStringBuilder(Address) { Pooling = false }.ConnectionString;
    }

    public override string Address { get; }

    public static SqliteFile Start(string service)
    {
        var file = new SqliteFile(Directory.CreateTempSubdirectory($"specrig-{service}-").FullName);
        Rig.Ending(file.Down);
        return file;
    }

    internal Task Down()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    public override async Task Discover(IReadOnlyList<string> keep, CancellationToken cancel)
    {
        await using var connection = await Attached(cancel);
        var tables = (await connection.Rows(
                """
                SELECT name FROM pragma_table_list
                WHERE schema = 'main' AND type = 'table' AND name NOT LIKE 'sqlite\_%' ESCAPE '\' AND name NOT LIKE 'specrig\_%' ESCAPE '\'
                """,
                row => row.GetString(0), cancel))
            .Where(table => table != "__EFMigrationsHistory" && !keep.Contains(table)).ToList();
        var inserts = new List<string>();
        foreach (var table in tables)
        {
            if (!(await connection.Rows($"SELECT EXISTS (SELECT 1 FROM main.{Quoted(table)})", row => row.GetBoolean(0), cancel)).Single())
            {
                continue;
            }
            var columns = string.Join(", ", await connection.Rows(
                $"SELECT name FROM pragma_table_xinfo({Literal(table)}, 'main') WHERE hidden = 0 ORDER BY cid",
                row => Quoted(row.GetString(0)), cancel));
            var copy = $"{Copies}.t{inserts.Count}";
            await connection.Execute($"CREATE TABLE {copy} AS SELECT {columns} FROM main.{Quoted(table)}", cancel);
            inserts.Add($"INSERT INTO main.{Quoted(table)} ({columns}) SELECT {columns} FROM {copy}");
        }
        var counted = (await connection.Rows("SELECT EXISTS (SELECT 1 FROM main.sqlite_schema WHERE name = 'sqlite_sequence')", row => row.GetBoolean(0), cancel)).Single();
        var sequences = counted
            ? (await connection.Rows("SELECT name, seq FROM main.sqlite_sequence", row => (Table: row.GetString(0), Seq: row.GetInt64(1)), cancel))
                .Where(sequence => tables.Contains(sequence.Table)).ToList()
            : null;
        baseline = new Baseline(tables, inserts, sequences);
        await Reset(cancel);
    }

    public override async Task Reset(CancellationToken cancel)
    {
        var (tables, inserts, sequences) = baseline ?? throw new InvalidOperationException("sqlite is reset before it is migrated");
        await using var connection = await Attached(cancel);
        await connection.Execute("PRAGMA foreign_keys = OFF", cancel);
        await connection.Execute("BEGIN IMMEDIATE", cancel);
        await Lift(connection, cancel);
        var triggers = await connection.Rows("SELECT name, sql FROM main.sqlite_schema WHERE type = 'trigger'",
            row => (Name: row.GetString(0), Sql: row.GetString(1)), cancel);
        foreach (var trigger in triggers)
        {
            await connection.Execute($"DROP TRIGGER main.{Quoted(trigger.Name)}", cancel);
        }
        foreach (var table in tables)
        {
            await connection.Execute($"DELETE FROM main.{Quoted(table)}", cancel);
        }
        foreach (var insert in inserts)
        {
            await connection.Execute(insert, cancel);
        }
        if (sequences is not null)
        {
            await connection.Execute($"DELETE FROM main.sqlite_sequence WHERE name IN ({string.Join(", ", tables.Select(Literal))})", cancel);
            foreach (var (table, seq) in sequences)
            {
                await connection.Execute($"INSERT INTO main.sqlite_sequence (name, seq) VALUES ({Literal(table)}, {seq})", cancel);
            }
        }
        foreach (var trigger in triggers)
        {
            await connection.Execute(trigger.Sql, cancel);
        }
        await connection.Execute("COMMIT", cancel);
    }

    public override async Task Refuse(Refusal refusal, CancellationToken cancel)
    {
        var writes = refusal.Writes.Select(write => write.ToString().ToLowerInvariant()).ToList();
        var name = $"specrig_refuse_{string.Join('_', writes)}";
        var quota = $"relation = {Literal(refusal.Table)} AND name = {Literal(name)}";
        try
        {
            await using var connection = await Connected(cancel);
            await connection.Execute("BEGIN IMMEDIATE", cancel);
            await connection.Execute(
                $"CREATE TABLE IF NOT EXISTS main.{Refusals} (relation text, name text, remaining int NOT NULL, PRIMARY KEY (relation, name))", cancel);
            await connection.Execute(
                $"""
                INSERT INTO main.{Refusals} (relation, name, remaining) VALUES ({Literal(refusal.Table)}, {Literal(name)}, {refusal.Rows})
                ON CONFLICT (relation, name) DO UPDATE SET remaining = excluded.remaining
                """,
                cancel);
            foreach (var write in writes)
            {
                var trigger = Quoted($"{name}_{write}_on_{refusal.Table}");
                await connection.Execute($"DROP TRIGGER IF EXISTS main.{trigger}", cancel);
                await connection.Execute(
                    $"""
                    CREATE TRIGGER main.{trigger} BEFORE {write} ON {Quoted(refusal.Table)} BEGIN
                        UPDATE {Refusals} SET remaining = remaining - 1 WHERE {quota};
                        SELECT RAISE(ABORT, {Literal($"specrig: {write} on {refusal.Table} is refused")})
                        WHERE (SELECT remaining FROM {Refusals} WHERE {quota}) < 0;
                    END
                    """,
                    cancel);
            }
            await connection.Execute("COMMIT", cancel);
        }
        catch (SqliteException e)
        {
            throw new InvalidOperationException($"sqlite: cannot refuse writes to {refusal.Table}: {e.Message}", e);
        }
    }

    public override async Task Lift(CancellationToken cancel)
    {
        await using var connection = await Connected(cancel);
        await connection.Execute("BEGIN IMMEDIATE", cancel);
        await Lift(connection, cancel);
        await connection.Execute("COMMIT", cancel);
    }

    async Task<SqliteConnection> Connected(CancellationToken cancel)
    {
        var connection = new SqliteConnection(own);
        await connection.OpenAsync(cancel);
        return connection;
    }

    async Task<SqliteConnection> Attached(CancellationToken cancel)
    {
        var connection = await Connected(cancel);
        await connection.Execute($"ATTACH DATABASE {Literal(Path.Combine(dir, "baseline.db"))} AS {Copies}", cancel);
        return connection;
    }

    static async Task Lift(DbConnection connection, CancellationToken cancel)
    {
        var triggers = await connection.Rows(
            """SELECT name FROM main.sqlite_schema WHERE type = 'trigger' AND name LIKE 'specrig\_refuse\_%' ESCAPE '\'""",
            row => row.GetString(0), cancel);
        foreach (var trigger in triggers)
        {
            await connection.Execute($"DROP TRIGGER main.{Quoted(trigger)}", cancel);
        }
        await connection.Execute($"DROP TABLE IF EXISTS main.{Refusals}", cancel);
    }

    static string Quoted(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    static string Literal(string value) => $"'{value.Replace("'", "''")}'";

    sealed record Baseline(IReadOnlyList<string> Tables, IReadOnlyList<string> Inserts, IReadOnlyList<(string Table, long Seq)>? Sequences);
}
