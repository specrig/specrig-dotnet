using System.Data.Common;
using MySqlConnector;

namespace Specrig;

// Not MySql: that is a namespace of Oracle's MySql.Data, which EF Core 10 reaches MySQL through.
public sealed class Mysql(string setting) : SqlDatabase(setting)
{
    public string? Address { get; init; }

    public string Image { get; init; } = "mysql:8.4";

    internal override string Kind => "mysql";

    internal override async Task<string> Provisioned(string name)
    {
        var given = Address ?? Environment.GetEnvironmentVariable($"{name.ToUpperInvariant()}_TEST_MYSQL");
        var server = await Shared($"{given ?? Image}\n{name}", () => MySqlServer.Start(name, given, Image));
        var address = new MySqlConnectionStringBuilder(server.Address);
        var (host, port) = Gated(address.Server, (int)address.Port);
        (address.Server, address.Port) = (host, (uint)port);
        return address.ConnectionString;
    }

    // The service's own driver, MySqlConnector or Oracle's, says how it meets a server that refuses it.
    internal override Exception Refused(DbConnection connection)
    {
        using var probe = (DbConnection)Activator.CreateInstance(connection.GetType())!;
        probe.ConnectionString = new DbConnectionStringBuilder { ConnectionString = connection.ConnectionString, ["Pooling"] = false }.ConnectionString;
        try
        {
            probe.Open();
        }
        catch (DbException e)
        {
            return e;
        }
        return new InvalidOperationException("mysql: the gate is open while the database refuses connections");
    }
}
