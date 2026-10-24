using System.Text.RegularExpressions;
using Npgsql;
using Respawn;
using Respawn.Graph;
using Testcontainers.PostgreSql;

namespace Specrig;

sealed class PostgresServer
{
    const string Mark = "specrig ";

    readonly Lock gate = new();
    readonly string maintenance;
    readonly string database;
    Task? migration;
    Respawner? respawner;

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
        respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            TablesToIgnore = [new Table("__EFMigrationsHistory"), .. keep.Select(table => new Table(table))],
        });
    }

    public async Task Reset(CancellationToken cancel)
    {
        await using var connection = new NpgsqlConnection(Address);
        await connection.OpenAsync(cancel);
        await (respawner ?? throw new InvalidOperationException("postgres is reset before it is migrated")).ResetAsync(connection);
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

    static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    static string Quoted(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
}
