using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The resolver no longer holds the retained versions in dictionaries: it asks the catalog and the run's work
/// database. That is a rewrite of the component whose verdict decides whether a file is uploaded or pointed at
/// somebody else's bytes, so the safety net here is differential rather than example-based — generate versions with
/// every shape dedup has to reason about (the same content in several versions, entries marked unrecoverable, pack
/// members, two contents sharing one full hash so the second lands on …~1, a journal's confirmed blocks), then run
/// the frozen pre-rewrite implementation (<see cref="LegacyLocalDedupResolver"/>) and the SQLite one over the same
/// input and require every answer to agree.
/// <para>
/// The few tests after it pin what the differential comparison cannot see, because the old implementation had no
/// such thing: a finished upload leaving the in-flight table for a row in the work database, and the old synchronous
/// surface refusing to answer at all from a catalog-backed resolver.
/// </para>
/// </summary>
public sealed class LocalDedupResolverCatalogTests
{
    private static readonly BlobAddressScheme Plain = new(null, null);

    private static CancellationToken Ct => CancellationToken.None;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(64)]
    public async Task Catalog_Backed_Answers_Match_The_In_Memory_Maps(int seed)
    {
        var world = World.Generate(seed);
        var legacy = LegacyLocalDedupResolver.Build(Plain, world.Indexes, world.Confirmed);
        var (resolver, cleanup) = await TestResolver.From(Plain, world.Indexes, world.Confirmed, Ct);
        await using var _ = cleanup;

        var rng = new Random(seed * 7919);
        int blobHits = 0, packHits = 0, damaged = 0, prescreened = 0, misses = 0;
        for (var i = 0; i < 200; i++)
        {
            var c = world.Probes[rng.Next(world.Probes.Count)];

            var expectedBlob = legacy.TryFindExisting(c.FullHash, c.Length, c.Head, c.Tail);
            AssertSame(expectedBlob, await resolver.TryFindExistingAsync(c.FullHash, c.Length, c.Head, c.Tail, Ct));

            // A null tail on the probing side is as much a part of the criterion as a wrong one, so it is probed too.
            var tail = rng.Next(4) == 0 ? null : c.Tail;
            var expectedMember = legacy.TryFindPackMember(c.FullHash, c.Length, c.Head, tail);
            Assert.Equal(expectedMember, await resolver.TryFindPackMemberAsync(c.FullHash, c.Length, c.Head, tail, Ct));

            var expectedPrescreen = legacy.MayDeduplicate(c.Length, c.Head);
            Assert.Equal(expectedPrescreen, await resolver.MayDeduplicateAsync(c.Length, c.Head, Ct));

            var @ref = world.Refs[rng.Next(world.Refs.Count)];
            var expectedDamage = legacy.IsDamagedRef(@ref);
            Assert.Equal(expectedDamage, await resolver.IsDamagedRefAsync(@ref, Ct));

            if (expectedBlob is not null) blobHits++; else misses++;
            if (expectedMember is not null) packHits++;
            if (expectedDamage) damaged++;
            if (expectedPrescreen) prescreened++;
        }

        // The generator hands out every shape, so a change that quietly stopped exercising one of them would show up
        // here instead of turning the whole comparison into a tautology over misses.
        Assert.True(blobHits > 0 && misses > 0, $"blob hits {blobHits}, misses {misses}");
        Assert.True(packHits > 0, "no pack member ever matched");
        Assert.True(damaged > 0, "no damaged ref was ever probed");
        Assert.True(prescreened > 0, "the prescreen never said yes");

        int existing = 0, claims = 0, collisions = 0;
        for (var i = 0; i < 50; i++)
        {
            var c = world.Probes[rng.Next(world.Probes.Count)];
            var expected = await legacy.ResolveAsync(c.FullHash, c.Length, c.Head, c.Tail);
            var actual = await resolver.ResolveAsync(c.FullHash, c.Length, c.Head, c.Tail, Ct);

            Assert.Equal(expected.Ref, actual.Ref);
            Assert.Equal(expected.Collision, actual.Collision);
            Assert.Equal(expected.Exists, actual.Exists);
            AssertSame(expected.Existing, actual.Existing);

            if (expected.Exists)
            {
                existing++;
            }
            else
            {
                claims++;
                // Both claims are withdrawn before the next probe, so each one meets the same state on both sides.
                // Completing them instead would not compare like for like: the old resolver answers a later arrival
                // out of the in-flight table it never empties, the new one out of the work database's reservations
                // (which is what the two tests below are for).
                expected.Fail(Boom);
                actual.Fail(Boom);
            }

            if (expected.Collision)
                collisions++;
        }

        Assert.True(existing > 0 && claims > 0, $"dedup hits {existing}, claims {claims}");
        Assert.True(collisions > 0, "no probe ever had to step aside to …~N");
    }

    /// <summary>
    /// What replaces the in-flight table's second job. The old resolver kept every completed reservation for the
    /// life of the run and answered a later arrival off its completion; the claim now leaves the table the moment
    /// the finished upload is in <c>reservations</c>, and the next arrival with that content is answered from there.
    /// The row has to be committed *before* the claim goes, or the window between the two is one where a second file
    /// with the same content claims the same address and uploads over the first one's volumes.
    /// </summary>
    [Fact]
    public async Task A_Finished_Upload_Is_Answered_From_The_Work_Database()
    {
        var (catalog, work, cleanup) = await TestResolver.OpenAsync(Ct);
        await using var _ = cleanup;
        var resolver = new LocalDedupResolver(Plain, catalog, work);

        var first = await resolver.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t", Ct);
        Assert.False(first.Exists);
        await first.CompleteAsync(raw: true, volumes: 2, volumeSizes: [111, 222], Ct);

        // Committed, not merely enqueued: no flush here, and the row is read back on another connection.
        var row = await work.ReservationAsync(LocalDedupResolver.ContentKey("xxh128:d", 5, "xxh128:h", "xxh128:t"), Ct);
        Assert.NotNull(row);
        Assert.Equal(first.Ref, row.Ref);

        var second = await resolver.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t", Ct);
        Assert.True(second.Exists);
        Assert.Equal(first.Ref, second.Ref);
        Assert.True(second.Existing!.Raw);
        Assert.Equal(2, second.Existing.Volumes);
        Assert.Equal([111L, 222L], second.Existing.VolumeSizes);

        // And the same answer through the probe path, which is where a hit saves the compression as well.
        Assert.Equal(first.Ref, (await resolver.TryFindExistingAsync("xxh128:d", 5, "xxh128:h", "xxh128:t", Ct))!.Ref);
    }

    /// <summary>A latecomer that arrives while the first uploader is still going must still wait for it — the claim
    /// is in the table for exactly that stretch, and only leaves it once the reservation row can answer instead.</summary>
    [Fact]
    public async Task An_In_Flight_Claim_Still_Makes_The_Second_Arrival_Wait()
    {
        var (catalog, work, cleanup) = await TestResolver.OpenAsync(Ct);
        await using var _ = cleanup;
        var resolver = new LocalDedupResolver(Plain, catalog, work);

        var first = await resolver.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t", Ct);
        var secondTask = resolver.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t", Ct);
        Assert.False(secondTask.IsCompleted);

        await first.CompleteAsync(raw: false, volumes: 1, volumeSizes: [5], Ct);
        var second = await secondTask;

        Assert.True(second.Exists);
        Assert.Equal(first.Ref, second.Ref);
    }

    /// <summary>The prescreen's third source: content this run has already started on. It lives in the work
    /// database's <c>reserved_heads</c> now, where the answer is only as fresh as the last commit — a delay that can
    /// only cost one wasted compression, never a wrong address.</summary>
    [Fact]
    public async Task A_Head_Noted_In_Flight_Is_Seen_By_The_Prescreen()
    {
        var (catalog, work, cleanup) = await TestResolver.OpenAsync(Ct);
        await using var _ = cleanup;
        var resolver = new LocalDedupResolver(Plain, catalog, work);

        Assert.False(await resolver.MayDeduplicateAsync(100, "xxh128:hd", Ct));
        await resolver.NoteInFlightAsync(100, "xxh128:hd", Ct);
        await work.FlushAsync(Ct);

        Assert.True(await resolver.MayDeduplicateAsync(100, "xxh128:hd", Ct));
        Assert.False(await resolver.MayDeduplicateAsync(101, "xxh128:hd", Ct));   // length is part of the key
    }

    /// <summary>
    /// The synchronous surface is the compatibility shim the orchestrator is still on until Task 13, and it can only
    /// be answered out of the in-memory maps. Silently answering something plausible from a catalog-backed resolver
    /// is the one outcome worth refusing: a sync Complete would set the waiters' result and never write the
    /// reservation row, leaving a claim nothing can ever retire.
    /// </summary>
    [Fact]
    public async Task The_Synchronous_Surface_Refuses_A_Catalog_Backed_Resolver()
    {
        var (catalog, work, cleanup) = await TestResolver.OpenAsync(Ct);
        await using var _ = cleanup;
        var resolver = new LocalDedupResolver(Plain, catalog, work);

        Assert.Throws<InvalidOperationException>(() => resolver.MayDeduplicate(1, "xxh128:h"));
        Assert.Throws<InvalidOperationException>(() => resolver.NoteInFlight(1, "xxh128:h"));
        Assert.Throws<InvalidOperationException>(() => resolver.IsDamagedRef("data/x"));
        Assert.Throws<InvalidOperationException>(() => resolver.TryFindExisting("xxh128:f", 1, "xxh128:h", "xxh128:t"));
        Assert.Throws<InvalidOperationException>(() => resolver.TryFindPackMember("xxh128:f", 1, "xxh128:h", "xxh128:t"));

        var claim = await resolver.ResolveAsync("xxh128:d", 5, "xxh128:h", "xxh128:t", Ct);
        Assert.Throws<InvalidOperationException>(() => claim.Complete(raw: false, volumes: 1, volumeSizes: [5]));
        claim.Fail(Boom);
    }

    private static InvalidOperationException Boom => new("upload boom");

    private static void AssertSame(ResolvedBlob? expected, ResolvedBlob? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        // Field by field, not record equality: VolumeSizes is an IReadOnlyList, and the compiler-generated Equals
        // compares two lists by reference, which would pass for any two answers that came out of different objects.
        Assert.NotNull(actual);
        Assert.Equal(expected.Ref, actual.Ref);
        Assert.Equal(expected.Raw, actual.Raw);
        Assert.Equal(expected.Volumes, actual.Volumes);
        Assert.Equal(expected.VolumeSizes, actual.VolumeSizes);
    }

    /// <summary>One piece of content and the address content addressing gives it. Two contents may share a full hash
    /// (the <c>~1</c> case), which is why the ref is carried rather than derived.</summary>
    private sealed record Content(string FullHash, long Length, string Head, string Tail, string Ref);

    /// <summary>The generated input: the retained versions, the adopted journal's confirmed blocks, and the two
    /// pools the probes are drawn from.</summary>
    private sealed record World(
        IReadOnlyList<VersionIndex> Indexes, IReadOnlyList<ConfirmedBlob> Confirmed,
        IReadOnlyList<Content> Probes, IReadOnlyList<string> Refs)
    {
        private static readonly DateTimeOffset Mtime = new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));

        public static World Generate(int seed)
        {
            var rng = new Random(seed);
            var contents = new List<Content>();
            for (var h = 0; h < 8; h++)
            {
                var full = $"xxh128:full{h}";
                // One full hash, sometimes two different contents behind it: the residual collision the ~N suffix
                // exists for, and the only way a probe ever walks past the base address.
                for (var twin = 0; twin < 1 + rng.Next(2); twin++)
                {
                    contents.Add(new Content(
                        full, 1000 + h * 10 + twin, $"xxh128:head{h}-{twin}", $"xxh128:tail{h}-{twin}",
                        twin == 0 ? "data/" + full : $"data/{full}~{twin}"));
                }
            }

            // The address of an entry that carries no content identity at all (below). It is the first suffix past
            // the last full hash's twins, so a near miss on that hash walks straight onto it: an in-memory build
            // skipped such an entry before it ever looked at its storage, so the address is free, and anything that
            // treats it as taken sends brand new content off to an address nobody will ever look for it at.
            var lastHash = contents[^1].FullHash;
            var strayRef = $"data/{lastHash}~{contents.Count(c => c.FullHash == lastHash)}";

            var refs = new List<string> { "data/nowhere", "pack0", "pack1", "pack2", strayRef };
            var indexes = new List<VersionIndex>();
            for (var v = 1; v <= 4; v++)
            {
                var index = new VersionIndex { Version = v };
                var seq = 0;
                foreach (var c in contents)
                {
                    if (rng.Next(4) == 0)
                        continue;   // not every version holds every content

                    var path = $"v{v}/f{seq++}.dat";
                    if (rng.Next(4) == 0)
                    {
                        // A pack member: same content, stored inside somebody else's archive. Its tail is dropped
                        // now and then, the way an index written before tails existed has it.
                        var pack = $"pack{rng.Next(3)}";
                        index.Entries.Add(Entry(path, c) with
                        {
                            TailHash = rng.Next(5) == 0 ? null : c.Tail,
                            Storage = new StorageRef { Kind = "pack", Ref = pack, EntryName = path },
                        });
                    }
                    else
                    {
                        var volumes = 1 + rng.Next(3);
                        index.Entries.Add(Entry(path, c) with
                        {
                            Storage = new StorageRef
                            {
                                Kind = "blob", Ref = c.Ref, Raw = rng.Next(2) == 0, Volumes = volumes,
                                // Sometimes absent, the way an index written before volume sizes existed has it.
                                VolumeSizes = rng.Next(5) == 0
                                    ? []
                                    : [.. Enumerable.Range(0, volumes).Select(i => (long)(c.Length / volumes + i))],
                            },
                        });
                        refs.Add(c.Ref);
                    }

                    // Damage: an occupied name holding broken bytes. It must stay out of every dedup answer while
                    // still fending off collisions on its address.
                    if (rng.Next(6) == 0)
                        index.UnrecoverablePaths.Add(path);
                }

                // Three shapes that carry no content identity at all, and must take part in nothing.
                index.Entries.Add(new IndexEntry
                {
                    Path = $"v{v}/link", Kind = "symlink", Permissions = "0777", Mtime = Mtime, Target = "../x",
                });
                index.Entries.Add(Entry($"v{v}/unstored.dat", contents[0]) with { Storage = null });
                index.Entries.Add(new IndexEntry
                {
                    Path = $"v{v}/nohash.dat", Kind = "file", Length = 7, Mtime = Mtime, Permissions = "0644",
                    Storage = new StorageRef { Kind = "blob", Ref = strayRef },
                });
                indexes.Add(index);
            }

            // The adopted journal: content confirmed in the cloud but in no index yet. One of them repeats an
            // indexed content under a different address (the index must win), one occupies an address the index
            // already holds (the index must win there too), the rest are new.
            var probes = new List<Content>(contents);
            var confirmed = new List<ConfirmedBlob>();
            for (var i = 0; i < 3; i++)
            {
                var c = new Content(
                    $"xxh128:journal{i}", 500 + i, $"xxh128:jhead{i}", $"xxh128:jtail{i}", $"data/xxh128:journal{i}");
                probes.Add(c);
                refs.Add(c.Ref);
                confirmed.Add(new ConfirmedBlob(
                    c.FullHash, c.Length, c.Head, c.Tail, new ResolvedBlob(c.Ref, Raw: true, Volumes: 2, [400, 100])));
            }

            confirmed.Add(new ConfirmedBlob(
                contents[0].FullHash, contents[0].Length, contents[0].Head, contents[0].Tail,
                new ResolvedBlob("data/journal-elsewhere", Raw: false, Volumes: 1, [1])));
            refs.Add("data/journal-elsewhere");
            confirmed.Add(new ConfirmedBlob(
                "xxh128:journal-clash", 42, "xxh128:jhead-clash", "xxh128:jtail-clash",
                new ResolvedBlob(contents[0].Ref, Raw: false, Volumes: 1, [42])));
            probes.Add(new Content(
                "xxh128:journal-clash", 42, "xxh128:jhead-clash", "xxh128:jtail-clash", contents[0].Ref));

            // Misses: content nobody has ever seen, and near misses that share a full hash with content somebody has
            // — the ones that have to step aside rather than deduplicate.
            probes.Add(new Content("xxh128:unknown", 3, "xxh128:headU", "xxh128:tailU", "data/xxh128:unknown"));
            // The last one is the near miss that walks past every twin of the last full hash and onto strayRef.
            foreach (var c in contents.Take(4).Append(contents[^1]))
                probes.Add(c with { Length = c.Length + 1, Tail = c.Tail + "x" });

            return new World(indexes, confirmed, probes, refs);
        }

        private static IndexEntry Entry(string path, Content c) => new()
        {
            Path = path, Kind = "file", Length = c.Length, Mtime = Mtime, Permissions = "0644",
            FullHash = c.FullHash, HeadHash = c.Head, TailHash = c.Tail,
        };
    }
}
