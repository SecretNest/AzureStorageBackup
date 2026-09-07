namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Lifts an in-memory sequence into the <see cref="IAsyncEnumerable{T}"/> the differ now consumes. Hand-written rather
/// than pulled in from System.Linq.Async: one method against a whole extra package is a bad trade, and the tests are
/// the only place that needs it — production feeds the differ real SQLite cursors.
/// </summary>
internal static class AsyncEnumerableTestExtensions
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source)
    {
        foreach (var item in source)
            yield return await Task.FromResult(item);
    }
}
