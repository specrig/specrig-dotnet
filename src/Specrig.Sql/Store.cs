using System.Data.Common;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Specrig;

abstract class Store
{
    readonly Lock gate = new();
    Task? migration;

    public abstract string Address { get; }

    public Task Once(Func<Task> migrate)
    {
        lock (gate)
        {
            return migration ??= migrate();
        }
    }

    public abstract Task Discover(IReadOnlyList<string> keep, CancellationToken cancel);

    public abstract Task Reset(CancellationToken cancel);

    public abstract Task Refuse(Refusal refusal, CancellationToken cancel);

    public abstract Task Lift(CancellationToken cancel);

    public virtual void Forget()
    {
    }
}

abstract class Server(string maintenance, string database, string address) : Store
{
    const string Mark = "specrig ";

    public override string Address { get; } = address;

    protected abstract DbConnection Connect(string address);

    protected abstract Task<List<(string Name, string Mark, long Connected)>> Databases(DbConnection admin);

    protected abstract Task Create(DbConnection admin, string name, string mark);

    protected abstract Task Drop(DbConnection admin, string name);

    protected async Task Open(string stem, string where)
    {
        try
        {
            await using var admin = Connect(maintenance);
            await admin.OpenAsync();
            await Sweep(admin, stem);
            await Drop(admin, database);
            await Create(admin, database, $"{Mark}{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        }
        catch (Exception e) when (e is DbException or TimeoutException or SocketException)
        {
            throw new InvalidOperationException($"{where}: {e.Message}", e);
        }
        Rig.Ending(Down);
    }

    internal async Task Down()
    {
        Forget();
        await using var admin = Connect(maintenance);
        await admin.OpenAsync();
        await Drop(admin, database);
    }

    async Task Sweep(DbConnection admin, string stem)
    {
        var ours = new Regex($"^{Regex.Escape(stem)}_[0-9]+$");
        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        foreach (var (name, mark, connected) in await Databases(admin))
        {
            if (ours.IsMatch(name) && connected == 0 && mark.StartsWith(Mark) && long.TryParse(mark[Mark.Length..], out var at) && at < hourAgo)
            {
                await Drop(admin, name);
            }
        }
    }
}

static class Sql
{
    public static async Task Execute(this DbConnection connection, string sql, CancellationToken cancel = default, params object[] values)
    {
        await using var command = Command(connection, sql, values);
        await command.ExecuteNonQueryAsync(cancel);
    }

    public static async Task<List<T>> Rows<T>(this DbConnection connection, string sql, Func<DbDataReader, T> row, CancellationToken cancel = default, params object[] values)
    {
        await using var command = Command(connection, sql, values);
        await using var reader = await command.ExecuteReaderAsync(cancel);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancel))
        {
            rows.Add(row(reader));
        }
        return rows;
    }

    static DbCommand Command(DbConnection connection, string sql, object[] values)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values)
        {
            var parameter = command.CreateParameter();
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return command;
    }
}
