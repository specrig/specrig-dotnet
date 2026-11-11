using System.Data.Common;
using System.Net.Sockets;
using Npgsql;

namespace Specrig;

public sealed class Postgres(string setting) : SqlDatabase(setting)
{
    public string? Address { get; init; }

    public string Image { get; init; } = "postgres:17-alpine";

    internal override string Kind => "postgres";

    public Task RefuseCommit(string table, params Write[] writes) => Refuse(table, 0, true, writes);

    internal override async Task<string> Provisioned(string name)
    {
        var given = Address ?? Environment.GetEnvironmentVariable($"{name.ToUpperInvariant()}_TEST_POSTGRES");
        var server = await Shared($"{given ?? Image}\n{name}", () => PostgresServer.Start(name, given, Image));
        var address = new NpgsqlConnectionStringBuilder(server.Address);
        (address.Host, address.Port) = Gated(address.Host!, address.Port);
        return address.ConnectionString;
    }

    internal override Exception Refused(DbConnection connection) =>
        new NpgsqlException("the database refuses connections", new SocketException((int)SocketError.ConnectionRefused));
}
