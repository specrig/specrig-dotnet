using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Specrig;

sealed class Fault
{
    public Exception? Error { get; set; }

    public void Check()
    {
        if (Error is not null)
        {
            throw Error;
        }
    }
}

sealed class Refusing(Fault fault) : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        fault.Check();
        return result;
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        fault.Check();
        return ValueTask.FromResult(result);
    }
}

// A connection a transaction holds open is past opening, and fails at its commands.
sealed class Failing(Fault fault) : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        fault.Check();
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        fault.Check();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        fault.Check();
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        fault.Check();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        fault.Check();
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        fault.Check();
        return ValueTask.FromResult(result);
    }
}
