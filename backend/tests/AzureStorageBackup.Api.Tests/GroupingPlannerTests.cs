using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class GroupingPlannerTests
{
    private static PlannedFile F(string path, long length, string? hash = null) =>
        new(path, length, hash ?? "sha256:" + path);

    private static BackupPlan Plan(IEnumerable<PlannedFile> files, PlanOptions? options = null) =>
        new GroupingPlanner().Plan(files.ToList(), options);

    [Fact]
    public void Large_File_Goes_To_Single_Blob()
    {
        var plan = Plan([F("big.bin", 6_000_000)], new PlanOptions { SingleFileThresholdBytes = 5_000_000 });

        var blob = Assert.Single(plan.Blobs);
        Assert.Equal("big.bin", blob.Path);
        Assert.Equal("data/sha256:big.bin", blob.Ref);
        Assert.Empty(plan.Packs);
    }

    [Fact]
    public void Dont_Group_Match_Goes_To_Single_Blob_Even_If_Small()
    {
        var options = new PlanOptions { DontGroup = new IgnoreRuleSet(["*.iso"]) };

        var plan = Plan([F("small.iso", 100), F("small.txt", 100)], options);

        Assert.Contains(plan.Blobs, b => b.Path == "small.iso");
        Assert.Contains(plan.Packs.SelectMany(p => p.Members), m => m.Path == "small.txt");
    }

    [Fact]
    public void Small_Files_In_Same_Dir_Are_Packed_Together()
    {
        var plan = Plan([F("dir/a.txt", 100), F("dir/b.txt", 200)]);

        var pack = Assert.Single(plan.Packs);
        Assert.Equal(2, pack.Members.Count);
        Assert.Equal(300, pack.OriginalBytes);
        Assert.Contains(pack.Members, m => m.Path == "dir/a.txt" && m.EntryName == "dir/a.txt");
        Assert.Empty(plan.Blobs);
    }

    [Fact]
    public void Files_In_Different_Dirs_Are_Packed_Separately()
    {
        var plan = Plan([F("x/a.txt", 100), F("y/b.txt", 100)]);

        Assert.Equal(2, plan.Packs.Count);
        Assert.All(plan.Packs, p => Assert.Single(p.Members));
        Assert.Equal(["p0001", "p0002"], plan.Packs.Select(p => p.PackId));
    }

    [Fact]
    public void Group_Cap_Splits_Into_Multiple_Packs()
    {
        // Three 40-byte files in the same directory, per-group cap 100 → split into 2 packs (40+40 | 40).
        var plan = Plan(
            [F("d/a", 40), F("d/b", 40), F("d/c", 40)],
            new PlanOptions { GroupCapBytes = 100 });

        Assert.Equal(2, plan.Packs.Count);
        Assert.All(plan.Packs, p => Assert.True(p.OriginalBytes <= 100));
        Assert.Equal(3, plan.Packs.Sum(p => p.Members.Count));
    }

    [Fact]
    public void Pack_Members_Reference_Pack_Storage()
    {
        var plan = Plan([F("dir/a.txt", 100, "sha256:aaa")]);

        var member = plan.Packs[0].Members[0];
        Assert.Equal("sha256:aaa", member.FullHash);
        Assert.Equal("dir/a.txt", member.EntryName);
        Assert.Equal("p0001", plan.Packs[0].PackId);
    }

    // ---- Cross-path packing (hash-sharded directory trees) ----
    //
    // A problem the user hit in the field: Emby metadata has the shape .../library/09/<guid>/poster.jpg — enormous numbers of directories,
    // one or two files each. Splitting per directory drives the pack count toward the file count: 46,624 files produced tens of thousands of packs,
    // one 7z process plus one billed upload request each, and grouped packing (merging small files, reducing blob count) fell through entirely.

    /// <summary>Files matching the cross-path rule pack across directory boundaries: four small files sitting in four different directories should form only one pack.</summary>
    [Fact]
    public void Cross_Dir_Rule_Packs_Across_Directory_Boundaries()
    {
        var options = new PlanOptions
        {
            CrossDirGroup = new IgnoreRuleSet(["meta/**"]),
            GroupCapBytes = 10_000,
        };

        var plan = Plan(
        [
            F("meta/09/aaa/poster.jpg", 100),
            F("meta/09/bbb/poster.jpg", 100),
            F("meta/1a/ccc/poster.jpg", 100),
            F("meta/1a/ddd/poster.jpg", 100),
        ], options);

        var pack = Assert.Single(plan.Packs);
        Assert.Equal(4, pack.Members.Count);
        Assert.Empty(plan.Blobs);
    }

    /// <summary>The default (empty rule) must be byte for byte identical to historical behavior: still split per directory, one pack per directory.</summary>
    [Fact]
    public void Without_The_Rule_Each_Directory_Still_Gets_Its_Own_Pack()
    {
        var plan = Plan(
        [
            F("meta/09/aaa/poster.jpg", 100),
            F("meta/09/bbb/poster.jpg", 100),
        ], new PlanOptions { GroupCapBytes = 10_000 });

        Assert.Equal(2, plan.Packs.Count); // Different directories → two packs, exactly the shape being solved
    }

    /// <summary>Priority: don't-group > cross-path packing. "Don't group" says "should not be merged with anyone at all",
    /// and the cross-path rule must not overturn it.</summary>
    [Fact]
    public void Dont_Group_Outranks_Cross_Dir_Grouping()
    {
        var options = new PlanOptions
        {
            CrossDirGroup = new IgnoreRuleSet(["meta/**"]),
            DontGroup = new IgnoreRuleSet(["*.iso"]),
        };

        var plan = Plan([F("meta/a/x.iso", 100), F("meta/b/y.jpg", 100)], options);

        Assert.Equal("meta/a/x.iso", Assert.Single(plan.Blobs).Path); // don't-group wins
        Assert.Equal("meta/b/y.jpg", Assert.Single(Assert.Single(plan.Packs).Members).Path);
    }

    /// <summary>Cross-path packs each carry their own GroupKey: the orchestrator builds pools from it, and pools run concurrently.
    /// If they all shared one key, tens of thousands of cross-directory files would be stuffed into a single serial pool.</summary>
    [Fact]
    public void Cross_Dir_Packs_Get_Distinct_Group_Keys_So_They_Stay_Parallel()
    {
        var options = new PlanOptions
        {
            CrossDirGroup = new IgnoreRuleSet(["meta/**"]),
            GroupCapBytes = 150, // at most one file per pack
        };

        var plan = Plan([F("meta/a/1.jpg", 100), F("meta/b/2.jpg", 100), F("meta/c/3.jpg", 100)], options);

        Assert.Equal(3, plan.Packs.Count);
        Assert.Equal(3, plan.Packs.Select(p => p.GroupKey).Distinct().Count());
    }

    /// <summary>When packing per directory the GroupKey is the directory, and the orchestrator uses it to file same-directory packs into one pool (historical behavior).</summary>
    [Fact]
    public void By_Directory_Packs_Use_The_Directory_As_Group_Key()
    {
        var plan = Plan(
        [
            F("d/1.txt", 100),
            F("d/2.txt", 100),
        ], new PlanOptions { GroupCapBytes = 150 }); // split into two packs, but both belong to the same directory pool

        Assert.Equal(2, plan.Packs.Count);
        Assert.Equal(["d"], plan.Packs.Select(p => p.GroupKey).Distinct());
    }

    /// <summary>
    /// A single-file blob's full-content hash may be deferred to the compression pass (<see cref="PlannedFile.FullHash"/> is null),
    /// while <c>data/{hash}</c> is a content address — no hash means no address. Wiring it up wrong must blow up on the spot:
    /// build an empty <c>data/</c> address and upload it, and it would only be discovered on restore day, pointing at no blob.
    /// </summary>
    [Fact]
    public void Addressing_A_File_Whose_Hash_Was_Deferred_Fails_Loudly()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Plan([new PlannedFile("big.bin", 10_000_000, null)]));

        Assert.Contains("big.bin", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pack members, by contrast, are **not** rejected for a null hash: a symlink has no content hash to begin with (the diff always returns null for one),
    /// and symlinks can be packed — 7z stores the link itself. Widen the null rejection by one notch,
    /// and a single symlink pointing elsewhere is enough to bring down an entire backup run.
    /// </summary>
    [Fact]
    public void A_Pack_Member_Without_A_Content_Hash_Is_Still_Packed()
    {
        var plan = Plan([F("d/1.txt", 100), new PlannedFile("d/link", 0, null)]);

        Assert.Empty(plan.Blobs);
        Assert.Equal(["d/1.txt", "d/link"], plan.Packs.Single().Members.Select(m => m.Path));
    }

    /// <summary>
    /// The byte cap cannot hold the member count down: the smaller the files, the more members the same byte budget swallows.
    /// That pack's member list is held **in its entirety** in memory (compression needs it, re-verification needs it, retry after failure needs it too),
    /// and measured 7z member metadata alone is about 1.3 KB per member.
    /// </summary>
    [Fact]
    public void A_Pack_Is_Sealed_When_It_Hits_The_Member_Limit()
    {
        // Ten 1-byte files with the byte cap set sky-high — only the member-count limit can split this.
        var plan = Plan(
            Enumerable.Range(0, 10).Select(i => F($"d/{i}.txt", 1)),
            new PlanOptions { GroupCapBytes = long.MaxValue, MaxPackMembers = 4 });

        Assert.Equal([4, 4, 2], plan.Packs.Select(p => p.Members.Count));
        // Split it may be, but not one member may go missing.
        Assert.Equal(10, plan.Packs.Sum(p => p.Members.Count));
    }

    /// <summary>
    /// The path-byte limit is about a hard failure: member paths are passed to 7z one by one as argv, and going over gets a flat E2BIG from the kernel.
    /// The limit must be set in **bytes**, not member count — the wall moves as paths get longer: the measured 1.73 MB argv budget
    /// fits thirty-odd thousand 52-character paths, but only three thousand-odd 500-character ones.
    /// </summary>
    [Fact]
    public void A_Pack_Is_Sealed_When_It_Hits_The_Path_Byte_Limit()
    {
        // Each path "d/xx.txt" = 8 bytes + NUL = 9; budget 30 → 3 per pack.
        var plan = Plan(
            Enumerable.Range(0, 7).Select(i => F($"d/{i:D2}.txt", 1)),
            new PlanOptions { GroupCapBytes = long.MaxValue, MaxPackMembers = int.MaxValue, MaxPackPathBytes = 30 });

        Assert.Equal([3, 3, 1], plan.Packs.Select(p => p.Members.Count));
    }

    /// <summary>
    /// Paths are counted in UTF-8 bytes, not characters. A CJK path takes up to three bytes per character, so counting characters underestimates by more than a factor of two,
    /// and the consequence of underestimating is not a bigger pack, it is <c>E2BIG</c>: compression fails on the spot.
    /// </summary>
    [Fact]
    public void Path_Bytes_Are_Counted_As_Utf8_Not_Characters()
    {
        // "照片/01.jpg": 2 Chinese characters ×3 + "/01.jpg" 7 = 13 bytes + NUL = 14. Counting characters gives only 10.
        Assert.Equal(14, GroupingPlanner.EntryArgBytes("照片/01.jpg"));

        // Budget 28 = exactly two. Counting characters would make it look like a third fits (it only seals at 33 > 28).
        var plan = Plan(
            Enumerable.Range(0, 5).Select(i => F($"照片/{i:D2}.jpg", 1)),
            new PlanOptions { GroupCapBytes = long.MaxValue, MaxPackPathBytes = 28 });

        Assert.Equal([2, 2, 1], plan.Packs.Select(p => p.Members.Count));
    }

    /// <summary>
    /// The two new limits must be **a no-op** for ordinary backups. Under the defaults, files averaging 5 KB or more always hit the 100 MB one first,
    /// so the packing result of existing backups does not change by a single byte — that is the precondition for adding these two limits, not a side effect.
    /// </summary>
    [Fact]
    public void The_New_Limits_Do_Not_Change_Grouping_For_Ordinary_Files()
    {
        // 1000 files of 200 KB (200 MB in total) = under the defaults, split by 100 MB into two packs of 500 members each.
        var files = Enumerable.Range(0, 1000).Select(i => F($"d/{i:D4}.bin", 200 * 1024)).ToList();

        var withLimits = Plan(files);                                    // defaults: 20,000 members / 1 MB of paths
        var withoutLimits = Plan(files, new PlanOptions
        {
            MaxPackMembers = int.MaxValue,
            MaxPackPathBytes = long.MaxValue,
        });

        Assert.Equal(
            withoutLimits.Packs.Select(p => p.Members.Select(m => m.Path).ToList()),
            withLimits.Packs.Select(p => p.Members.Select(m => m.Path).ToList()));
        Assert.Equal(2, withLimits.Packs.Count);
    }

    /// <summary>
    /// When a single member exceeds a limit all by itself we must neither loop forever nor drop it: an item that does not fit still gets a pack of its own.
    /// (The byte limit has behaved this way all along; the two new ones must match it.)
    /// </summary>
    [Fact]
    public void An_Item_That_Alone_Exceeds_A_Limit_Still_Gets_Its_Own_Pack()
    {
        var longPath = "d/" + new string('x', 200) + ".txt";
        var plan = Plan(
            [F("d/a.txt", 1), F(longPath, 1), F("d/b.txt", 1)],
            new PlanOptions { GroupCapBytes = long.MaxValue, MaxPackPathBytes = 50 });

        // After sorting: a, b, then the over-long path — the first two share a pack, and the over-long one blows the budget on its own → a pack of its own.
        Assert.Equal(3, plan.Packs.Sum(p => p.Members.Count));
        Assert.Contains(plan.Packs, p => p.Members.Count == 1 && p.Members[0].Path == longPath);
    }

    /// <summary>
    /// A pack can hold only one compression mode, so compressible and non-compressible files in the same directory must be packed separately. Mixing them makes the rule
    /// effectively nonexistent for the files that got packed — which was exactly this feature's earlier defect (the whole pack was compressed with the configured -m… regardless).
    /// </summary>
    [Fact]
    public void Same_Dir_Splits_Into_A_Compressed_Pack_And_A_Store_Only_Pack()
    {
        var options = new PlanOptions { DontCompress = new IgnoreRuleSet(["*.jpg"]) };

        var plan = Plan(
            [F("d/a.jpg", 100), F("d/b.txt", 100), F("d/c.jpg", 100), F("d/e.txt", 100)],
            options);

        Assert.Equal(2, plan.Packs.Count);

        var compressed = Assert.Single(plan.Packs, p => !p.StoreOnly);
        Assert.Equal(["d/b.txt", "d/e.txt"], compressed.Members.Select(m => m.Path));

        var stored = Assert.Single(plan.Packs, p => p.StoreOnly);
        Assert.Equal(["d/a.jpg", "d/c.jpg"], stored.Members.Select(m => m.Path));

        // Both packs belong to the same directory; processing-pool membership must not split just because of compression mode.
        Assert.All(plan.Packs, p => Assert.Equal("d", p.GroupKey));
    }

    /// <summary>"Don't group" remains the strongest statement of intent: matches take the single-file blob route, where that route derives the compression mode itself from the same rules,
    /// and they never take part in packing at all.</summary>
    [Fact]
    public void Dont_Group_Outranks_Dont_Compress()
    {
        var options = new PlanOptions
        {
            DontGroup = new IgnoreRuleSet(["*.iso"]),
            DontCompress = new IgnoreRuleSet(["*.iso", "*.jpg"]),
        };

        var plan = Plan([F("d/x.iso", 100), F("d/y.jpg", 100)], options);

        Assert.Equal("d/x.iso", Assert.Single(plan.Blobs).Path);
        var pack = Assert.Single(plan.Packs);
        Assert.True(pack.StoreOnly);
        Assert.Equal("d/y.jpg", Assert.Single(pack.Members).Path);
    }

    /// <summary>Cross-path packing gets split the same way: what it ignores is directory boundaries, not compression mode.</summary>
    [Fact]
    public void Cross_Dir_Grouping_Also_Splits_By_Compressibility()
    {
        var options = new PlanOptions
        {
            CrossDirGroup = new IgnoreRuleSet(["meta/**"]),
            DontCompress = new IgnoreRuleSet(["*.jpg"]),
        };

        var plan = Plan(
            [F("meta/a/1.jpg", 100), F("meta/b/2.txt", 100), F("meta/c/3.jpg", 100)],
            options);

        Assert.Equal(2, plan.Packs.Count);
        Assert.Equal(["meta/b/2.txt"], Assert.Single(plan.Packs, p => !p.StoreOnly).Members.Select(m => m.Path));
        // Cross-directory merging still happens within one lane: the two jpgs live in different directories yet still land in the same pack.
        Assert.Equal(
            ["meta/a/1.jpg", "meta/c/3.jpg"],
            Assert.Single(plan.Packs, p => p.StoreOnly).Members.Select(m => m.Path));
    }

    /// <summary>
    /// Strict splitting, with **no minimum-member fallback**: even a lane holding just one member still becomes its own pack.
    /// In an incremental backup a directory may have just two changed files this round, and "two packs holding one member each" is an accepted normal case —
    /// this test nails that trade-off down so nobody later casually adds a "too small, merge it back" exception.
    /// </summary>
    [Fact]
    public void A_Lone_Odd_File_Still_Gets_Its_Own_Pack()
    {
        var options = new PlanOptions { DontCompress = new IgnoreRuleSet(["*.jpg"]) };

        var plan = Plan([F("d/a.jpg", 100), F("d/b.txt", 100)], options);

        Assert.Equal(2, plan.Packs.Count);
        Assert.All(plan.Packs, p => Assert.Single(p.Members));
    }

    /// <summary>
    /// When the rule **matches no file at all**, the packing result must be byte for byte identical to what it was before the rule existed — and in particular no
    /// empty second pack may appear out of nowhere. That is the precondition for adding the split, not a side effect (same intent as the The_New_Limits_… test).
    /// </summary>
    [Fact]
    public void Dont_Compress_That_Matches_Nothing_Leaves_Grouping_Unchanged()
    {
        var files = new[] { F("d/a.txt", 100), F("d/b.txt", 100), F("e/c.txt", 100) };

        var withRule = Plan(files, new PlanOptions { DontCompress = new IgnoreRuleSet(["*.nomatch"]) });
        var withoutRule = Plan(files);

        Assert.Equal(
            withoutRule.Packs.Select(p => p.Members.Select(m => m.Path).ToList()),
            withRule.Packs.Select(p => p.Members.Select(m => m.Path).ToList()));
        Assert.Equal(withoutRule.Packs.Select(p => p.PackId), withRule.Packs.Select(p => p.PackId));
        Assert.All(withRule.Packs, p => Assert.False(p.StoreOnly));
    }
}
