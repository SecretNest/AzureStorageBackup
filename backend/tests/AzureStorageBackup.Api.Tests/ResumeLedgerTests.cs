using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// <see cref="ResumeLedger"/> against <see cref="LegacyJournalResume"/>, the dictionaries it replaced.
/// <para>
/// The resume path is the one place in this repo where being wrong is not "one more upload" but a wrong index entry:
/// a lookup that says yes when the old table said no writes last run's content into the new version under a file that
/// has since changed. So the replacement is not checked case by case here — it is checked <b>against the thing it
/// replaces</b>, on randomly generated journals, with deliberately overlapping paths and member sets so that every
/// tie-breaking rule (newest volume wins, first record per path wins, a member set matched item for item) is actually
/// hit rather than assumed.
/// </para>
/// <para>
/// Only the <c>Ref</c> is compared, not the whole record: the ref is what the caller acts on (it becomes the index
/// entry's storage), and the record the ledger rebuilds out of SQLite is deliberately not byte-identical to the one
/// the journal line deserialized to — a pack record, for one, carries only the columns a resume needs.
/// </para>
/// </summary>
public sealed class ResumeLedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "asb-resumeledger-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => CancellationToken.None;

    public ResumeLedgerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leaked handle must not fail a test that already passed */ }
    }

    private Task<RunWorkDb> OpenAsync() => new RunWorkDbFactory(_dir).CreateAsync("run", Ct);

    // Small pools, on purpose: the interesting cases are the collisions (the same path recorded in two volumes, the
    // same member set sealed twice), and drawing from a wide alphabet would make them vanishingly rare.
    private static readonly string[] Paths =
        [.. Enumerable.Range(0, 40).Select(i => $"dir{i % 5}/file{i}.bin")];

    private static readonly string[] Hashes = [.. Enumerable.Range(0, 12).Select(i => $"full{i}")];

    // Head and tail are drawn independently of the full hash. Deriving them from it would make the two extra content
    // tests in FindBlob unfalsifiable: every probe that matched on the full hash would match on all four.
    private static readonly string?[] Heads = ["head0", "head1", "head2", null];
    private static readonly string?[] Tails = ["tail0", "tail1", "tail2", null];

    private static readonly long[] Lengths = [0, 1, 100, 4096, 1_000_003];

    private static readonly DateTimeOffset[] Mtimes =
        [.. Enumerable.Range(0, 6).Select(i => DateTimeOffset.UnixEpoch.AddHours(i))];

    /// <summary>A fixed pool of member sets so that packs really do collide; probes perturb them to get the misses.</summary>
    private static IReadOnlyList<IReadOnlyList<JournalMember>> MemberSets(Random rng) =>
    [
        [],   // no members at all: neither implementation may store or match it
        .. Enumerable.Range(0, 20).Select(_ =>
            (IReadOnlyList<JournalMember>)[.. Enumerable.Range(0, 1 + rng.Next(4)).Select(_ => Member(rng))]),
    ];

    private static JournalMember Member(Random rng)
    {
        var path = Paths[rng.Next(Paths.Length)];
        return new JournalMember(path, path, Hashes[rng.Next(Hashes.Length)], Lengths[rng.Next(Lengths.Length)]);
    }

    private static JournalRecord Record(Random rng, int seq, IReadOnlyList<IReadOnlyList<JournalMember>> memberSets)
    {
        if (rng.Next(100) < 60)
        {
            var full = Hashes[rng.Next(Hashes.Length)];
            var length = Lengths[rng.Next(Lengths.Length)];
            return new JournalRecord
            {
                Kind = "blob",
                Ref = $"data/blob-{seq}",
                // A one-in-fifteen chance of a record with no path or no content identity: a crash can leave a
                // half-written line, and both implementations have to drop it rather than store a row nothing matches.
                Path = rng.Next(15) == 0 ? null : Paths[rng.Next(Paths.Length)],
                FullHash = rng.Next(15) == 0 ? null : full,
                HeadHash = Heads[rng.Next(Heads.Length)],
                TailHash = Tails[rng.Next(Tails.Length)],
                Length = length,
                Raw = rng.Next(2) == 0,
                // A quarter of the records predate the mtime field, which is the case FindUntouchedBlob must refuse.
                MtimeUtcTicks = rng.Next(4) == 0 ? null : Mtimes[rng.Next(Mtimes.Length)].UtcTicks,
                Volumes = 1 + rng.Next(3),
                VolumeSizes = [length],
            };
        }

        var members = memberSets[rng.Next(memberSets.Count)];
        return new JournalRecord
        {
            Kind = "pack",
            Ref = $"p{seq:D12}",
            StoreOnly = rng.Next(2) == 0,
            Members = members,
            Volumes = 1 + rng.Next(2),
            VolumeSizes = [1000 + seq],
        };
    }

    /// <summary>Three volumes with distinct start times, records dealt out among them at random.</summary>
    private static IReadOnlyList<JournalContent> Volumes(
        Random rng, int count, IReadOnlyList<IReadOnlyList<JournalMember>> memberSets)
    {
        var buckets = new List<JournalRecord>[3];
        for (var i = 0; i < buckets.Length; i++)
            buckets[i] = [];
        for (var seq = 0; seq < count; seq++)
            buckets[rng.Next(buckets.Length)].Add(Record(rng, seq, memberSets));

        return [.. buckets.Select((records, i) => new JournalContent(
            new JournalHeader
            {
                RunId = $"run{i}", ConfigId = 1, StartedAt = DateTimeOffset.UnixEpoch.AddHours(i),
                BaselineVersion = 0, LocalRoot = "/data/src", EncryptionIdentity = "plain",
            },
            records))];
    }

    [Fact]
    public async Task The_ledger_answers_exactly_what_the_dictionaries_answered()
    {
        var rng = new Random(20260907);
        var memberSets = MemberSets(rng);
        var volumes = Volumes(rng, 300, memberSets);
        var legacy = LegacyJournalResume.FromVolumes(volumes);

        await using var work = await OpenAsync();
        // The same order FromVolumes chains them in: newest first, so "the first record for a path wins" lands as
        // "the newest volume wins" on both sides.
        foreach (var volume in volumes.OrderByDescending(v => v.Header.StartedAt))
            foreach (var record in volume.Records)
                await work.InsertResumeRecordAsync(record, Ct);
        await work.FlushAsync(Ct);
        var ledger = new ResumeLedger(work);

        Assert.Equal(legacy.IsEmpty, await ledger.IsEmptyAsync(Ct));
        Assert.Equal(legacy.RecordCount, await ledger.RecordCountAsync(Ct));

        // Probes are drawn half from records that really were written (so hits happen at all) and half from the raw
        // pools (so misses are hit for every reason: wrong path, wrong hash, wrong head, wrong tail, wrong length).
        var blobs = volumes.SelectMany(v => v.Records).Where(r => r.Kind == "blob").ToList();

        var hits = 0;
        for (var i = 0; i < 200; i++)
        {
            var from = rng.Next(2) == 0 ? blobs[rng.Next(blobs.Count)] : null;
            var path = from?.Path ?? Paths[rng.Next(Paths.Length)];
            var full = from?.FullHash ?? Hashes[rng.Next(Hashes.Length)];
            var length = from?.Length ?? Lengths[rng.Next(Lengths.Length)];
            var head = (from is null ? Heads[rng.Next(Heads.Length)] : from.HeadHash) ?? "head0";
            var tail = (from is null ? Tails[rng.Next(Tails.Length)] : from.TailHash) ?? "tail0";

            var expected = legacy.FindBlob(path, full, length, head, tail);
            var actual = await ledger.FindBlobAsync(path, full, length, head, tail, Ct);
            Assert.Equal(expected?.Ref, actual?.Ref);
            if (expected is not null)
                hits++;
        }
        Assert.True(hits > 0, "the generated journal produced no FindBlob hit at all: the comparison proved nothing");

        hits = 0;
        for (var i = 0; i < 200; i++)
        {
            var from = rng.Next(2) == 0 ? blobs[rng.Next(blobs.Count)] : null;
            var path = from?.Path ?? Paths[rng.Next(Paths.Length)];
            var mtime = from?.MtimeUtcTicks is { } ticks
                ? new DateTimeOffset(ticks, TimeSpan.Zero)
                : Mtimes[rng.Next(Mtimes.Length)];
            var length = from?.Length ?? Lengths[rng.Next(Lengths.Length)];

            var expected = legacy.FindUntouchedBlob(path, mtime, length);
            var actual = await ledger.FindUntouchedBlobAsync(path, mtime, length, Ct);
            Assert.Equal(expected?.Ref, actual?.Ref);
            if (expected is not null)
                hits++;
        }
        Assert.True(
            hits > 0, "the generated journal produced no FindUntouchedBlob hit at all: the comparison proved nothing");

        hits = 0;
        for (var i = 0; i < 200; i++)
        {
            var members = memberSets[rng.Next(memberSets.Count)];
            var probe = rng.Next(3) switch
            {
                // Reversed: the same set in another order, which must not match — PackInfo.Members is written in
                // member order and a key blind to order would put the info file at odds with the archive.
                0 => (IReadOnlyList<JournalMember>)[.. members.Reverse()],
                1 when members.Count > 0 => [.. members.Skip(1)],   // one member short
                _ => members,
            };

            var expected = legacy.FindPack(probe);
            var actual = await ledger.FindPackAsync(probe, Ct);
            Assert.Equal(expected?.Ref, actual?.Ref);
            if (expected is not null)
                hits++;
        }
        Assert.True(hits > 0, "the generated journal produced no FindPack hit at all: the comparison proved nothing");

        // The dedup path reaches the very same rows by content instead of by path — the resolver probes
        // resume_blobs directly, which is what the old ConfirmedBlobs list was assembled for. Same claim about the
        // same rows in a fourth shape: everything the old dictionaries would have handed the dedup table is findable
        // by its content identity, and nothing else is.
        foreach (var b in legacy.ConfirmedBlobs())
        {
            var found = await work.ResumeBlobByContentAsync(b.FullHash, b.Length, b.HeadHash, b.TailHash, Ct);
            Assert.Equal(b.Blob.Ref, found?.Ref);
        }
    }

    /// <summary>A work database nobody fed a journal into: every lookup misses, and the caller's "is there anything to
    /// resume at all" test says no. The orchestrator asks that one before it stats a file, so a wrong answer here is a
    /// stat per file on every ordinary backup.</summary>
    [Fact]
    public async Task An_unfed_ledger_is_empty_and_finds_nothing()
    {
        await using var work = await OpenAsync();
        var ledger = new ResumeLedger(work);

        Assert.True(await ledger.IsEmptyAsync(Ct));
        Assert.Equal(0, await ledger.RecordCountAsync(Ct));
        Assert.Null(await ledger.FindBlobAsync("a.bin", "aaa", 100, "haaa", "taaa", Ct));
        Assert.Null(await ledger.FindUntouchedBlobAsync("a.bin", DateTimeOffset.UnixEpoch, 100, Ct));
        Assert.Null(await ledger.FindPackAsync([new JournalMember("a.txt", "a.txt", "ha", 5)], Ct));
        Assert.Null(await work.ResumeBlobByContentAsync("aaa", 100, "haaa", "taaa", Ct));
    }
}
