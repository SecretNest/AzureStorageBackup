namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Lifts an in-memory sequence into the <see cref="IAsyncEnumerable{T}"/> the differ now consumes, and drains one back
/// into a list for an assertion. Hand-written rather than pulled in from System.Linq.Async: two methods against a whole
/// extra package is a bad trade, and the tests are the only place that needs them — production feeds the differ real
/// SQLite cursors and streams the answers straight back out.
/// </summary>
internal static class AsyncEnumerableTestExtensions
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source)
    {
        foreach (var item in source)
            yield return await Task.FromResult(item);
    }

    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
            items.Add(item);
        return items;
    }
}
