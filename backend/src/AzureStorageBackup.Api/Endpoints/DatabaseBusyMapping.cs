using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>
/// Maps "the SQLite write lock was held by someone else for longer than the command timeout" to 503 plus a sentence
/// the operator can act on, instead of the bare 500 it used to be.
///
/// <para>
/// SQLite allows exactly one writer at a time, WAL or not (WAL only stops readers and the writer from shutting each
/// other out — see <see cref="Services.SqliteJournalMode"/>). A backup run commits its version index as one row
/// holding the whole serialized index, and on a saturated disk that single write can hold the lock for tens of
/// seconds. Everything else that wants to write in that window — the scheduler's log trim, and, the reason this
/// exists, a config edit the user is sitting in front of — waits, and gives up at the 30-second command timeout with
/// <c>SQLITE_BUSY</c>.
/// </para>
/// <para>
/// What the user saw before this: Save greyed out, then nothing. The request did eventually come back, as a 500 with
/// an empty body, which the frontend could only render as "Internal Server Error" — indistinguishable from a bug, and
/// no hint that simply trying again in a minute works. 503 with an explicit message says the two things that matter:
/// nothing was written, and waiting is the fix.
/// </para>
/// <para>
/// The same narrow shape as <see cref="SecretUnavailableMapping"/> above, and for the same reason: recognize one
/// exception, rethrow everything else untouched, so no other failure semantics change.
/// </para>
/// </summary>
public static class DatabaseBusyMapping
{
    // SQLITE_BUSY = 5 (someone else holds the lock), SQLITE_LOCKED = 6 (a table in this connection is locked).
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    public sealed record DatabaseBusyError(string error, string code);

    public static readonly DatabaseBusyError Payload = new(
        "The database was busy for too long and the change was not saved. This happens while a backup, check or "
        + "repair is writing to it on a loaded disk. Nothing was changed — try again once that job has settled.",
        "database_busy");

    /// <summary>
    /// Walks the inner-exception chain, because the two ways this application writes wrap it differently: the bulk
    /// operations (<c>ExecuteDeleteAsync</c>) let the <see cref="SqliteException"/> straight through, while
    /// <c>SaveChangesAsync</c> — which is what a config edit goes through — buries it inside a
    /// <c>DbUpdateException</c>. Matching only the bare exception would have missed exactly the path this was written for.
    /// </summary>
    private static bool IsBusy(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is SqliteException { SqliteErrorCode: SqliteBusy or SqliteLocked })
                return true;
        return false;
    }

    public static IApplicationBuilder UseDatabaseBusyMapping(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception ex) when (IsBusy(ex))
            {
                // As above: once the response has started the status code is no longer ours to set.
                if (context.Response.HasStarted)
                    throw;

                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(Payload);
            }
        });
}
