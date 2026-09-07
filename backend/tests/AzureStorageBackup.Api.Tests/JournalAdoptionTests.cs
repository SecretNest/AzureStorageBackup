using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The "adopt or void" decision table applied when a volume is opened (<see cref="BackupRunControl.OpenJournalAsync"/>).
/// <para>
/// Get any one of the four terms backwards and the whole backup suite still passes: the voiding branch quietly deletes last
/// run's work, and the adopting branch quietly claims somebody else's work as its own. The ConfigId term matters most — it is
/// the only branch that touches state **not belonging to this run**, and once Task 11's orphan sweep landed, getting it wrong
/// amounts to "deleting the work a suspended run has already done". So the whole table is pinned down here, term by term.
/// </para>
/// <para>Pure temp directory: no cloud, no 7z, no Azurite. The work database is a temp file too — the adopted records are
/// streamed into it, and <see cref="BackupRunControl.Resume"/> answers out of it.</para>
/// </summary>
public sealed class JournalAdoptionTests : IDisposable
{
    private const int AccountId = 77;
    private const string Container = "photos";
    private const int ConfigId = 5;
    private const int Baseline = 3;
    private const string LocalRoot = "/srv/photos";
    private const string Identity = "plain";

    private readonly string _dir;
    private readonly BackupJournalStore _store;

    public JournalAdoptionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "asb-adopt-" + Guid.NewGuid().ToString("N"));
        _store = new BackupJournalStore(Path.Combine(_dir, "journal"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>This run's scratch database. Held by the caller with <c>await using</c>, because the ledger keeps
    /// reading from it for as long as the assertions do.</summary>
    private Task<RunWorkDb> WorkAsync() =>
        new RunWorkDbFactory(Path.Combine(_dir, "work")).CreateAsync(Guid.NewGuid().ToString("N"), default);

    /// <param name="full">The content identity to record. Defaults to one derived from the path, so that a volume is
    /// identified by which file it names; pass it explicitly to record **the same path with different content**.</param>
    private static JournalRecord Blob(string path, string? full = null)
    {
        var id = full ?? path;
        return new JournalRecord
        {
            Kind = "blob", Ref = "data/" + id, Path = path, FullHash = "f" + id, HeadHash = "h" + id,
            TailHash = "t" + id, Length = 100, Volumes = 1, VolumeSizes = [100],
        };
    }

    /// <summary>Plant a ready-made journal volume on disk. All four header fields match by default; break them one at a time with named arguments.</summary>
    private async Task<string> PlantAsync(
        string runId, int configId = ConfigId, int baseline = Baseline, string localRoot = LocalRoot,
        string identity = Identity, string path = "a.bin", string? full = null, DateTimeOffset? startedAt = null)
    {
        await using (var journal = await _store.CreateAsync(AccountId, Container, runId, new JournalHeader
        {
            RunId = runId,
            ConfigId = configId,
            StartedAt = startedAt ?? DateTimeOffset.UtcNow,
            BaselineVersion = baseline,
            LocalRoot = localRoot,
            EncryptionIdentity = identity,
        }, default))
        {
            await journal.AppendAsync(Blob(path, full), default);
        }
        return _store.PathFor(AccountId, Container, runId);
    }

    /// <summary>Start a new run and take it through opening the volume. All four terms use the values that match.</summary>
    private async Task<BackupRunControl> OpenAsync(RunWorkDb work, string runId = "run-new", bool firstRun = false)
    {
        var control = new BackupRunControl(_store, ConfigId, runId);
        await control.OpenJournalAsync(
            AccountId, Container, Baseline, LocalRoot, Identity, DateTimeOffset.UtcNow, work, default, firstRun);
        return control;
    }

    [Fact]
    public async Task Nothing_on_disk_means_nothing_to_resume_and_nothing_to_sweep()
    {
        await using var work = await WorkAsync();
        await using var control = await OpenAsync(work);

        Assert.Null(control.Resume);
        Assert.False(control.SweepNeeded);
    }

    /// <summary>
    /// A first run: not one journal volume on disk (nothing adopted, nothing voided), and it still has to sweep.
    /// <para>
    /// This is the term propping up the delete-config endpoint's promise — deleting a config throws away every journal for this
    /// container, leaving those "in the cloud, not in the index" blocks with nobody protecting them, and the first cleanup after
    /// the config is recreated is precisely the **backup tail** one: the other two terms are necessarily false at that moment.
    /// Without this term those blocks can only wait for a Cleanup schedule the user may never have configured.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_first_run_sweeps_even_with_no_journal_in_sight()
    {
        await using var work = await WorkAsync();
        await using var control = await OpenAsync(work, firstRun: true);

        Assert.Null(control.Resume);
        Assert.True(control.SweepNeeded);
    }

    [Fact]
    public async Task All_four_terms_matching_is_adopted()
    {
        var planted = await PlantAsync("run-old");

        await using var work = await WorkAsync();
        await using var control = await OpenAsync(work);

        Assert.Equal(1, await control.Resume!.RecordCountAsync(default));
        Assert.NotNull(await control.Resume.FindBlobAsync("a.bin", "fa.bin", 100, "ha.bin", "ta.bin", default));
        // Something was adopted → the container holds "in the cloud, not in the index" blocks, so the tail should sweep (Task 11).
        Assert.True(control.SweepNeeded);
        // Adoption is **read-only**: that volume stays on disk untouched — this run does not copy it, truncate it, or delete it.
        Assert.True(File.Exists(planted), "an adopted journal must be left on disk");
    }

    [Fact]
    public async Task A_foreign_config_id_is_voided()
    {
        // (AccountId, ContainerName) is a unique index in AppDbContext, so a container has at most one config —
        // which means this can only be the residue left by "a config deleted and then recreated on the same container".
        var planted = await PlantAsync("run-old", configId: ConfigId + 1);

        await using var work = await WorkAsync();
        await using var control = await OpenAsync(work);

        Assert.Null(control.Resume);
        Assert.False(File.Exists(planted), "a journal belonging to another config must be voided");
        Assert.True(control.SweepNeeded);
    }

    [Fact]
    public async Task A_stale_baseline_version_is_voided()
    {
        // The baseline changed = somebody else already completed a whole run, and the references in that volume should long since be the index's business.
        var planted = await PlantAsync("run-old", baseline: Baseline - 1);

        await using var work = await WorkAsync();
        await using var control = await OpenAsync(work);

        Assert.Null(control.Resume);
        Assert.False(File.Exists(planted), "a journal from another baseline must be voided");
        Assert.True(control.SweepNeeded);
    }

    [Fact]
    public async Task A_different_local_root_is_voided()
    {
        // Change the root directory and the same relative path no longer means the same file.
        var planted = await PlantAsync("run-old", localRoot: LocalRoot + "-moved");

        await using var work = await WorkAsync();
        await using var control = await OpenAsync(work);

        Assert.Null(control.Resume);
        Assert.False(File.Exists(planted), "a journal taken under another local root must be voided");
        Assert.True(control.SweepNeeded);
    }

    [Fact]
    public async Task A_different_encryption_identity_is_voided()
    {
        // Change the key and the address space changes with it; not one ref in the old volume still lines up.
        var planted = await PlantAsync("run-old", identity: "keyed:abc");

        await using var work = await WorkAsync();
        await using var control = await OpenAsync(work);

        Assert.Null(control.Resume);
        Assert.False(File.Exists(planted), "a journal written under another key must be voided");
        Assert.True(control.SweepNeeded);
    }

    [Fact]
    public async Task Every_still_valid_volume_is_adopted_not_just_the_first()
    {
        // Repeated suspend/resume piles up several volumes, and every one of them counts.
        await PlantAsync("run-old-1", path: "a.bin");
        await PlantAsync("run-old-2", path: "b.bin");

        await using var work = await WorkAsync();
        await using var control = await OpenAsync(work);

        Assert.Equal(2, await control.Resume!.RecordCountAsync(default));
        Assert.NotNull(await control.Resume.FindBlobAsync("a.bin", "fa.bin", 100, "ha.bin", "ta.bin", default));
        Assert.NotNull(await control.Resume.FindBlobAsync("b.bin", "fb.bin", 100, "hb.bin", "tb.bin", default));
    }

    /// <summary>
    /// The same path recorded with different content in two volumes — the file was modified between two suspends —
    /// and the **newer volume** must win, whichever order the volumes come back off disk in.
    /// <para>
    /// This is the half of the rule that lives here rather than in the ledger: volumes are read in file-name order,
    /// and a file name is a runId, a freshly generated GUID prefix that says nothing about age. So the run has to sort
    /// them by <see cref="JournalHeader.StartedAt"/> before feeding them in, or "which version of this path counts
    /// this run" is decided by a dice roll — the same input re-uploading different files on two consecutive runs.
    /// The theory runs it both ways round precisely so that passing by luck of the file names is not possible.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_newest_volume_wins_a_path_recorded_twice(bool newestSortsFirst)
    {
        var older = DateTimeOffset.UnixEpoch;
        await PlantAsync(
            newestSortsFirst ? "run-a" : "run-z", path: "a.bin", full: "zzz", startedAt: older.AddHours(1));
        await PlantAsync(
            newestSortsFirst ? "run-z" : "run-a", path: "a.bin", full: "aaa", startedAt: older);

        await using var work = await WorkAsync();
        await using var control = await OpenAsync(work);

        Assert.Equal(1, await control.Resume!.RecordCountAsync(default));
        Assert.Equal(
            "data/zzz", (await control.Resume.FindBlobAsync("a.bin", "fzzz", 100, "hzzz", "tzzz", default))!.Ref);
        Assert.Null(await control.Resume.FindBlobAsync("a.bin", "faaa", 100, "haaa", "taaa", default));
    }

    [Fact]
    public async Task Volumes_are_judged_one_by_one_not_as_a_batch()
    {
        var mine = await PlantAsync("run-old-mine", path: "a.bin");
        var foreign = await PlantAsync("run-old-foreign", configId: ConfigId + 1, path: "b.bin");

        await using var work = await WorkAsync();
        await using var control = await OpenAsync(work);

        Assert.Equal(1, await control.Resume!.RecordCountAsync(default));
        Assert.NotNull(await control.Resume.FindBlobAsync("a.bin", "fa.bin", 100, "ha.bin", "ta.bin", default));
        Assert.Null(await control.Resume.FindBlobAsync("b.bin", "fb.bin", 100, "hb.bin", "tb.bin", default));
        Assert.True(File.Exists(mine));
        Assert.False(File.Exists(foreign));
    }

    [Fact]
    public async Task An_adopted_volume_survives_a_run_that_never_commits()
    {
        // Suspended or failed tail (CompleteAsync was never called): the adopted volume must still be there untouched,
        // or the next run can never reuse the blocks it recorded again — and those blocks really are in the cloud.
        var planted = await PlantAsync("run-old");

        await using var work = await WorkAsync();
        var control = await OpenAsync(work);
        await control.DisposeAsync();

        Assert.True(File.Exists(planted));
        Assert.True(File.Exists(_store.PathFor(AccountId, Container, "run-new")));
    }

    /// <summary>
    /// This run's runId collides with the volume on disk: after adopting it we must **append to it**, not open a fresh volume that overwrites it.
    /// <para>
    /// Today's RunId is a freshly generated GUID prefix, so it cannot collide; Task 15 "automatically carry on at startup" will
    /// reuse the runId of the suspended run (so the run's identity in the UI stays the same), and reusing it collides. After a
    /// truncation the current run is still correct (the records are already in the work database); what breaks is the guarantee on disk — so this case pins **what is left in the file**, not what is in Resume.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_volume_carrying_our_own_run_id_is_appended_to_not_truncated()
    {
        var planted = await PlantAsync("run-same");

        await using var work = await WorkAsync();
        await using (var control = await OpenAsync(work, "run-same"))
        {
            Assert.Equal(1, await control.Resume!.RecordCountAsync(default));
            await control.RecordBlobAsync(
                "b.bin", "data/b", "fb", "hb", "tb", 7, DateTimeOffset.UnixEpoch, 1, false, [7], default);
        }

        var content = await BackupJournal.ReadAsync(planted, default);
        Assert.NotNull(content);
        Assert.Equal(ConfigId, content!.Header.ConfigId);
        Assert.Equal(["a.bin", "b.bin"], content.Records.Select(r => r.Path));
    }

    [Fact]
    public async Task Committing_deletes_the_adopted_volume_along_with_its_own()
    {
        // They only retire once the index commit succeeds — this is what keeps the "still there" case above from being an empty claim.
        var planted = await PlantAsync("run-old");

        await using var work = await WorkAsync();
        var control = await OpenAsync(work);
        await control.CompleteAsync();
        await control.DisposeAsync();

        Assert.False(File.Exists(planted));
        Assert.False(File.Exists(_store.PathFor(AccountId, Container, "run-new")));
    }
}
