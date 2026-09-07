using System.Globalization;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Retention used to work out what it may delete by reading every retained version's whole index back into objects
/// and subtracting sets in memory; it now asks the catalog one question per storage kind. What those two ways of
/// asking have to agree on is pinned here against the old code itself: the deletion sets against the set difference
/// the old loop amounted to (<c>allRefsOf(retired) - allRefsOf(retained)</c>, computed in C# from the very same
/// <see cref="VersionIndex"/> objects the catalog was filled from), and the compactor's input against
/// <see cref="LegacyLiveByPack"/> - a verbatim copy of the loop that used to build it.
/// <para>
/// No Azurite and no blob client: both answers are pure functions of a catalog file, which is exactly why the
/// computation sits in its own <c>internal static</c> method rather than inline in the cleanup.
/// </para>
/// </summary>
public sealed class RetentionCleanerCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "asb-retcat-" + Guid.NewGuid().ToString("N"));

    public RetentionCleanerCatalogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leaked handle must not fail a test that already passed */ }
    }

    private static readonly int[] Retired = [1, 3];
    private static readonly int[] Retained = [2, 4];

    // ---- the fixture ------------------------------------------------------------------------------------------

    /// <summary>A syntactically real xxh128 hash (32 hex digits) derived from a seed, so the index serializer stores
    /// it the packed way it stores a production hash rather than falling back to the "some other algorithm" path.</summary>
    private static string Hash(string seed) =>
        "xxh128:" + string.Concat(seed.Select(c => (c % 16).ToString("x", CultureInfo.InvariantCulture)))
            .PadRight(32, '7')[..32];

    private static IndexEntry Blob(string path, string reference) => new()
    {
        Path = path, Kind = "file", Length = 4, Permissions = "0644", FullHash = Hash(reference),
        Storage = new StorageRef { Kind = "blob", Ref = reference },
    };

    private static IndexEntry Packed(string path, string pack, string? entryName, long length, string? fullHash) => new()
    {
        Path = path, Kind = "file", Length = length, Permissions = "0644", FullHash = fullHash,
        Storage = new StorageRef { Kind = "pack", Ref = pack, EntryName = entryName },
    };

    /// <summary>
    /// Four versions whose refs deliberately straddle the retired/retained line in every way that matters: a ref only
    /// one retired version has (deletable), one two retired versions share (deletable - "referenced twice" is not
    /// "referenced by a survivor"), one a retired and a retained version share (must survive), one only a retained
    /// version has, plus the pack shapes the compactor's input turns on - the same content at two paths in one crate,
    /// the same member name in two versions (the newer must win), a member with no full hash at all, and an entry
    /// with no storage.
    /// </summary>
    private static List<VersionIndex> FourVersions() =>
    [
        new VersionIndex
        {
            Version = 1,
            Entries =
            [
                Blob("only1.bin", "data/only1"),
                Blob("shared13.bin", "data/shared13"),
                Blob("live12.bin", "data/live12"),
                Packed("p13/a.bin", "p13", "a.bin", 7, Hash("p13a")),
                Packed("plive/x.bin", "plive", "x.bin", 10, Hash("xold")),
                new IndexEntry { Path = "meta.txt", Kind = "file", Length = 0, Permissions = "0644" },
            ],
        },
        new VersionIndex
        {
            Version = 2,
            Entries =
            [
                Blob("live12.bin", "data/live12"),
                Blob("only2.bin", "data/only2"),
                Packed("plive/x.bin", "plive", "x.bin", 10, Hash("xold")),
                // Identical content at a second path: it dedups to the same full hash but is still its own member,
                // which is why the compactor's input is keyed by entry name and not by hash.
                Packed("plive/copy.bin", "plive", "copy.bin", 10, Hash("xold")),
                // No full hash: the old loop skipped such an entry before it ever added a member, so the crate it
                // names appears in no member list - while still being a referenced crate that must not be deleted.
                Packed("nohash.bin", "pnohash", "nohash.bin", 3, null),
            ],
        },
        new VersionIndex
        {
            Version = 3,
            Entries =
            [
                Blob("shared13.bin", "data/shared13"),
                Blob("live34.bin", "data/live34"),
                Packed("p13/b.bin", "p13", "b.bin", 8, Hash("p13b")),
            ],
        },
        new VersionIndex
        {
            Version = 4,
            Entries =
            [
                Blob("live34.bin", "data/live34"),
                // The same member name as version 2's, with different content: the newest version's copy is the one a
                // restore would extract, so it is the one compaction must weigh.
                Packed("plive/x.bin", "plive", "x.bin", 20, Hash("xnew")),
                // No entry name - the member is then known by its path, on both sides of the comparison.
                Packed("p4/y.bin", "p4", null, 9, Hash("p4y")),
            ],
        },
    ];

    private async Task<VersionCatalog> CatalogOfAsync(IEnumerable<VersionIndex> versions)
    {
        var catalog = await VersionCatalog.OpenAsync(Path.Combine(_dir, "catalog.db"), readOnly: false, default);
        foreach (var index in versions)
        {
            using var reader = new IndexStreamReader(new MemoryStream(LegacyIndexSerializer.SerializeIndex(index)));
            await catalog.ImportVersionAsync(index.Version, identity: index.Version, reader, default);
        }

        return catalog;
    }

    /// <summary>Every ref of one storage kind these versions name - the half of the old criterion that was a
    /// <c>HashSet</c> filled by walking each index entry by entry.</summary>
    private static HashSet<string> RefsOf(IEnumerable<VersionIndex> versions, string kind) =>
        versions.SelectMany(v => v.Entries)
            .Where(e => e.Storage is not null && e.Storage.Kind == kind)
            .Select(e => e.Storage!.Ref)
            .ToHashSet(StringComparer.Ordinal);

    private static List<VersionIndex> Only(IEnumerable<VersionIndex> versions, IReadOnlyCollection<int> wanted) =>
        versions.Where(v => wanted.Contains(v.Version)).ToList();

    private static IEnumerable<string> Sorted(IEnumerable<string> refs) => refs.OrderBy(r => r, StringComparer.Ordinal);

    // ---- Test 1: the deletion candidates are the set difference ------------------------------------------------

    [Fact]
    public async Task Candidates_are_the_retired_refs_minus_the_retained_ones()
    {
        var versions = FourVersions();
        await using var catalog = await CatalogOfAsync(versions);

        var candidates = await RetentionCleaner.CandidatesAsync(catalog, Retired, default);

        foreach (var kind in new[] { "blob", "pack" })
        {
            var expected = RefsOf(Only(versions, Retired), kind);
            expected.ExceptWith(RefsOf(Only(versions, Retained), kind));
            var actual = kind == "blob" ? candidates.RetiredOnlyBlobs : candidates.RetiredOnlyPacks;
            Assert.Equal(Sorted(expected), Sorted(actual));
        }

        // Spelled out as well as computed: a difference that came out empty on both sides would satisfy the loop above.
        Assert.Equal(["data/only1", "data/shared13"], Sorted(candidates.RetiredOnlyBlobs));
        Assert.Equal(["p13"], Sorted(candidates.RetiredOnlyPacks));
    }

    // ---- Test 2: the other half of the criterion, the orphan ---------------------------------------------------

    /// <summary>
    /// The old criterion was "delete what no retained version references", which swept up two different things at
    /// once: what the retired versions alone referenced, and what nothing in the container ever referenced. The
    /// candidate query only answers the first, so the second is still read out of the catalog - otherwise an orphan
    /// sweep would quietly stop sweeping the moment this changed.
    /// </summary>
    [Fact]
    public async Task Anything_no_version_references_is_deletable_and_anything_a_retained_version_holds_is_not()
    {
        var versions = FourVersions();
        await using var catalog = await CatalogOfAsync(versions);

        var candidates = await RetentionCleaner.CandidatesAsync(catalog, Retired, default);

        Assert.True(candidates.IsDeletableBlob("data/only1"));       // retired-only
        Assert.True(candidates.IsDeletableBlob("data/nobody"));      // an orphan: in no index at all
        Assert.False(candidates.IsDeletableBlob("data/live12"));     // shared with a retained version
        Assert.False(candidates.IsDeletableBlob("data/only2"));
        Assert.True(candidates.IsDeletablePack("p13"));
        Assert.True(candidates.IsDeletablePack("porphan"));
        Assert.False(candidates.IsDeletablePack("plive"));
        // Referenced only by an entry with no full hash - no member list, but the crate is still in use.
        Assert.False(candidates.IsDeletablePack("pnohash"));

        // And with nothing in the catalog at all, everything in the container is an orphan - the shape a container
        // that has never committed a version is swept with.
        Assert.True(RetentionCleaner.CleanupCandidates.NoVersions.IsDeletableBlob("data/live12"));
        Assert.True(RetentionCleaner.CleanupCandidates.NoVersions.IsDeletablePack("plive"));
    }

    // ---- Test 3: the compactor gets the same nested dictionary as before ---------------------------------------

    /// <summary>
    /// The loop <see cref="RetentionCleaner"/> used to build <c>liveByPack</c> with, copied verbatim (down to the
    /// comment on the key) so that the assertion below compares against the old behaviour rather than against a
    /// restatement of the new one.
    /// </summary>
    private static Dictionary<string, Dictionary<string, LivePackMember>> LegacyLiveByPack(IEnumerable<VersionIndex> retained)
    {
        var liveByPack = new Dictionary<string, Dictionary<string, LivePackMember>>(StringComparer.Ordinal);
        foreach (var vi in retained)
        {
            foreach (var e in vi.Entries)
            {
                if (e.Storage is null)
                    continue;
                if (e.Storage.Kind == "pack")
                {
                    if (e.FullHash is not null)
                    {
                        var members = liveByPack.TryGetValue(e.Storage.Ref, out var m)
                            ? m
                            : liveByPack[e.Storage.Ref] = new Dictionary<string, LivePackMember>(StringComparer.Ordinal);
                        // Group by entryName (unique within a pack): identical content at different paths dedups to the same fullHash but is still two members, so hash cannot be the key.
                        var entryName = e.Storage.EntryName ?? e.Path;
                        members[entryName] = new LivePackMember(entryName, e.Length, e.FullHash);
                    }
                }
            }
        }

        return liveByPack;
    }

    [Fact]
    public async Task Live_pack_members_match_the_loop_that_used_to_build_them()
    {
        var versions = FourVersions();
        await using var catalog = await CatalogOfAsync(versions);

        // The cleanup's own order: the retired versions leave the catalog first, so what is left to stream is exactly
        // the retained ones - which is what makes this a query instead of a loop with a filter in it.
        foreach (var version in Retired)
            await catalog.RemoveVersionAsync(version, default);

        var actual = await RetentionCleaner.LiveByPackAsync(catalog, default);
        var expected = LegacyLiveByPack(Only(versions, Retained));

        Assert.Equal(Sorted(expected.Keys), Sorted(actual.Keys));
        foreach (var (pack, members) in expected)
        {
            Assert.Equal(Sorted(members.Keys), Sorted(actual[pack].Keys));
            foreach (var (name, member) in members)
                Assert.Equal(member, actual[pack][name]);
        }

        // Again spelled out, because two identically wrong sides would agree: the newest version's copy of a member
        // wins, a second path in the same crate is its own member, a member with no entry name is known by its path,
        // and a hash-less member is in no list at all.
        Assert.Equal(new LivePackMember("x.bin", 20, Hash("xnew")), actual["plive"]["x.bin"]);
        Assert.Equal(new LivePackMember("copy.bin", 10, Hash("xold")), actual["plive"]["copy.bin"]);
        Assert.Equal(new LivePackMember("p4/y.bin", 9, Hash("p4y")), actual["p4"]["p4/y.bin"]);
        Assert.DoesNotContain("pnohash", actual.Keys);
        Assert.DoesNotContain("p13", actual.Keys);
    }
}
