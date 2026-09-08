using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The table behind within-run, cross-pack dedup of packed members. The criteria match
/// <see cref="LocalDedupResolver.TryFindPackMember"/>: fullHash + length + head + tail, all four strictly
/// equal, and a missing component counts as unequal.
/// <para>
/// Every case runs over a real <see cref="PackLeaderStore"/> in a temp directory rather than a substitute: the
/// store <b>is</b> the "who came first" half of the table now, and a fake of it would pin the test against a
/// second implementation of the one thing under test.
/// </para>
/// </summary>
public sealed class PackAliasTableTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "asb-packleaders-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort; it is temp space */ }
    }

    private Task<PackLeaderStore> NewStoreAsync() =>
        PackLeaderStore.CreateAsync(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".aliases.db"), default);

    [Fact]
    public async Task First_Occurrence_Becomes_Leader_And_Is_Not_An_Alias()
    {
        await using var store = await NewStoreAsync();
        var table = new PackAliasTable(store);

        // First occurrence of this content: the caller packs it as usual.
        Assert.False(await table.TryClaimAsync("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "a/x.txt", default));
        Assert.Empty(table.AliasesByLeader);
    }

    [Fact]
    public async Task Second_Occurrence_Of_Same_Content_Becomes_An_Alias_Of_The_First()
    {
        await using var store = await NewStoreAsync();
        var table = new PackAliasTable(store);
        await table.TryClaimAsync("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "a/x.txt", default);

        Assert.True(await table.TryClaimAsync("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "c/z.txt", default));

        var (leader, aliases) = Assert.Single(table.AliasesByLeader);
        Assert.Equal("a/x.txt", leader);
        Assert.Equal(["c/z.txt"], aliases.Select(a => a.Path));
    }

    [Fact]
    public async Task Many_Aliases_All_Hang_On_The_Same_Leader()
    {
        await using var store = await NewStoreAsync();
        var table = new PackAliasTable(store);
        await table.TryClaimAsync("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "a/x.txt", default);
        await table.TryClaimAsync("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "b/y.txt", default);
        await table.TryClaimAsync("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "c/z.txt", default);

        var (leader, aliases) = Assert.Single(table.AliasesByLeader);
        Assert.Equal("a/x.txt", leader);
        Assert.Equal(["b/y.txt", "c/z.txt"], aliases.Select(a => a.Path));
    }

    // Vary one of the four components at a time: none of these may be merged. Getting it wrong means
    // the index points at someone else's content and restore hands back wrong data.
    [Theory]
    [InlineData("xxh128:bb", 100L, "xxh128:hh", "xxh128:tt")]  // different fullHash
    [InlineData("xxh128:aa", 101L, "xxh128:hh", "xxh128:tt")]  // different length
    [InlineData("xxh128:aa", 100L, "xxh128:zz", "xxh128:tt")]  // different head
    [InlineData("xxh128:aa", 100L, "xxh128:hh", "xxh128:zz")]  // different tail
    public async Task Any_Differing_Component_Prevents_Aliasing(
        string full, long length, string head, string tail)
    {
        await using var store = await NewStoreAsync();
        var table = new PackAliasTable(store);
        await table.TryClaimAsync("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "a/x.txt", default);

        Assert.False(await table.TryClaimAsync(full, length, head, tail, "c/z.txt", default));
        Assert.Empty(table.AliasesByLeader);
    }

    // A missing component means no participation — that is exactly how members without a tail in old
    // indexes are kept out. The cost is only that the content gets stored one more time, and that is
    // the direction we want.
    [Theory]
    [InlineData(null, "xxh128:hh", "xxh128:tt")]
    [InlineData("xxh128:aa", null, "xxh128:tt")]
    [InlineData("xxh128:aa", "xxh128:hh", null)]
    public async Task A_Missing_Component_Never_Participates(string? full, string? head, string? tail)
    {
        await using var store = await NewStoreAsync();
        var table = new PackAliasTable(store);

        // Neither registered as a leader...
        Assert.False(await table.TryClaimAsync(full, 100, head, tail, "a/x.txt", default));
        // ...nor recognized by a second call that is missing the same component.
        Assert.False(await table.TryClaimAsync(full, 100, head, tail, "c/z.txt", default));
        Assert.Empty(table.AliasesByLeader);
        // And nothing reached the store either: an incomplete identity must not occupy a leader row that a
        // later, complete one would then be aliased onto.
        Assert.Equal(0, CountRows(store));
    }

    [Fact]
    public async Task A_Leader_Without_Aliases_Does_Not_Occupy_A_List()
    {
        await using var store = await NewStoreAsync();
        var table = new PackAliasTable(store);
        await table.TryClaimAsync("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "a/x.txt", default);
        await table.TryClaimAsync("xxh128:bb", 100, "xxh128:hh", "xxh128:tt", "b/y.txt", default);
        await table.TryClaimAsync("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "c/z.txt", default);

        // Only leaders that actually have aliases go into this table: a first backup has hundreds of
        // thousands of leaders, and giving each one an empty List wastes tens of MB for nothing.
        Assert.Equal(["a/x.txt"], table.AliasesByLeader.Keys);
    }

    /// <summary>
    /// A leader survives the commit that ends its batch. The store keeps one transaction open across thousands of
    /// claims, so every claim is answered partly from committed rows and partly from the transaction's own
    /// uncommitted ones, and the boundary between the two moves as the run goes on. What must never happen is that
    /// crossing it changes the answer — a leader that stops being found is a second copy of content already packed,
    /// and worse, a leader with aliases already hanging off it that a later duplicate would now lead a second group
    /// of aliases to. The run never calls <see cref="PackLeaderStore.Flush"/>, but it commits on exactly the same
    /// code path every 2 000 claims, which is what this drives directly rather than by claiming 2 000 times.
    /// </summary>
    [Fact]
    public async Task A_Leader_Is_Still_Found_After_The_Batch_Is_Committed()
    {
        await using var store = await NewStoreAsync();
        var table = new PackAliasTable(store);
        Assert.False(await table.TryClaimAsync("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "a/x.txt", default));

        store.Flush();

        Assert.True(await table.TryClaimAsync("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", "c/z.txt", default));
        var (leader, aliases) = Assert.Single(table.AliasesByLeader);
        Assert.Equal("a/x.txt", leader);
        Assert.Equal(["c/z.txt"], aliases.Select(a => a.Path));

        // ...and a *new* leader claimed after that commit opens the next batch rather than being lost with the
        // closed one: two committed rows, the one from before the flush and the one from after.
        Assert.False(await table.TryClaimAsync("xxh128:bb", 100, "xxh128:hh", "xxh128:tt", "b/y.txt", default));
        Assert.Equal(2, CountRows(store));
    }

    /// <summary>
    /// The reason the leader map moved into SQLite (Task 25): a first backup makes every packed file its own
    /// leader, so the in-memory dictionary this replaced held one entry per file — Task 23's benchmark measured
    /// ~56 MB of live managed heap per 100 000 files. What is pinned here is the structural property behind that
    /// number, not the number: 200 000 distinct contents leave the table's own dictionary completely empty, and
    /// all 200 000 leader rows are in the store's file.
    /// </summary>
    [Fact]
    public async Task Two_Hundred_Thousand_Distinct_Claims_Leave_Nothing_In_Memory()
    {
        await using var store = await NewStoreAsync();
        var table = new PackAliasTable(store);

        for (var i = 0; i < 200_000; i++)
        {
            var claimed = await table.TryClaimAsync(
                $"xxh128:{i:x8}", i, "xxh128:hh", "xxh128:tt", $"d{i / 1000:D4}/f{i:D6}.bin", default);
            // Distinct content: not one of them may be an alias.
            if (claimed)
                Assert.Fail($"claim {i} was treated as a duplicate");
        }

        // The half that stays in memory is bounded by duplicates, and there are none.
        Assert.Empty(table.AliasesByLeader);
        Assert.Equal(200_000, CountRows(store));
    }

    /// <summary>Counts the store's rows from a <b>second</b> connection, which sees only committed rows — hence the
    /// <see cref="PackLeaderStore.Flush"/> first, to end the batch the run would otherwise only commit on
    /// dispose (and dispose deletes the file).</summary>
    private static long CountRows(PackLeaderStore store)
    {
        store.Flush();
        using var connection = new SqliteConnection(CatalogSql.ConnectionString(store.Path, readOnly: true));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pack_leaders;";
        return (long)command.ExecuteScalar()!;
    }
}
