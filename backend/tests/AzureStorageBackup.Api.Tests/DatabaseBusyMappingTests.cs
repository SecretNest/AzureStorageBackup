using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// "database is locked" reaching the browser as a bare 500 is the whole reason this middleware exists: a user editing
/// a backup while a run committed its version index saw Save do nothing, then an "Internal Server Error" that gave
/// them no reason to simply try again. Each test below pins one of the two shapes the exception actually arrives in.
/// </summary>
public class DatabaseBusyMappingTests
{
    private static async Task<HttpResponseMessage> ThroughMiddlewareAsync(Exception thrown)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .Configure(app =>
                {
                    app.UseDatabaseBusyMapping();
                    app.Run(_ => throw thrown);
                }))
            .StartAsync();

        return await host.GetTestClient().GetAsync("/");
    }

    /// <summary>The bulk-delete shape: ExecuteDeleteAsync lets the SqliteException through unwrapped. This is what the
    /// scheduler's log trim hit on the reporting user's NAS.</summary>
    [Fact]
    public async Task Bare_Busy_Exception_Becomes_503_With_A_Reason()
    {
        var response = await ThroughMiddlewareAsync(new SqliteException("SQLite Error 5: 'database is locked'.", 5));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DatabaseBusyMapping.DatabaseBusyError>();
        Assert.Equal("database_busy", body!.code);
        Assert.Contains("Nothing was changed", body.error);
    }

    /// <summary>The shape that matters for the reported bug, and the one a naive `catch (SqliteException)` would have
    /// missed entirely: SaveChangesAsync — which is how a config edit is written — buries it in a DbUpdateException.</summary>
    [Fact]
    public async Task Busy_Wrapped_In_DbUpdateException_Becomes_503_Too()
    {
        var response = await ThroughMiddlewareAsync(new DbUpdateException(
            "An error occurred while saving the entity changes.",
            new SqliteException("SQLite Error 5: 'database is locked'.", 5)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("database_busy",
            (await response.Content.ReadFromJsonAsync<DatabaseBusyMapping.DatabaseBusyError>())!.code);
    }

    /// <summary>SQLITE_LOCKED (6), the sibling of BUSY, is the same story from the caller's side.</summary>
    [Fact]
    public async Task Locked_Error_Code_Is_Mapped_As_Well()
    {
        var response = await ThroughMiddlewareAsync(new SqliteException("SQLite Error 6: 'database table is locked'.", 6));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>Everything else keeps bubbling untouched. The narrowness is the point — this must not quietly become a
    /// catch-all that turns unrelated failures into 503s (see the note on SecretUnavailableMapping).</summary>
    [Fact]
    public async Task Other_Sqlite_Errors_Are_Not_Swallowed()
    {
        await Assert.ThrowsAsync<SqliteException>(() => ThroughMiddlewareAsync(
            new SqliteException("SQLite Error 19: 'UNIQUE constraint failed'.", 19)));
    }
}
