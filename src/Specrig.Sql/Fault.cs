using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Specrig;

sealed class Fault
{
    Exception? met;

    public Func<DbConnection, Exception>? Error
    {
        get;
        set
        {
            field = value;
            met = null;
        }
    }

    public void Check(DbConnection connection)
    {
        if (Error is not null)
        {
            throw met ??= Error(connection);
        }
    }
}

sealed class Refusing(Fault fault) : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        fault.Check(connection);
        return result;
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        fault.Check(connection);
        return ValueTask.FromResult(result);
    }
}

// A connection a transaction holds open is past opening, and fails at its commands.
sealed class Failing(Fault fault) : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        fault.Check(command.Connection!);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        fault.Check(command.Connection!);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        fault.Check(command.Connection!);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        fault.Check(command.Connection!);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        fault.Check(command.Connection!);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        fault.Check(command.Connection!);
        return ValueTask.FromResult(result);
    }
}
