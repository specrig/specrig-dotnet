using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Specrig;

public sealed class Sqlite(string setting) : SqlDatabase(setting)
{
    internal override string Kind => "sqlite";

    internal override async Task<string> Provisioned(string name) =>
        (await Shared(name, () => Task.FromResult(SqliteFile.Start(name)))).Address;

    internal override Exception Refused(DbConnection connection) =>
        new SqliteException("SQLite Error 14: 'unable to open database file'.", 14);
}
