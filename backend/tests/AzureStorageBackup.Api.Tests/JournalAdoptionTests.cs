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
/// <para>Pure temp directory: no cloud, no 7z, no Azurite.</para>
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

    private static JournalRecord Blob(string path) => new()
    {
        Kind = "blob", Ref = "data/" + path, Path = path, FullHash = "f" + path, HeadHash = "h" + path,
        TailHash = "t" + path, Length = 100, Volumes = 1, VolumeSizes = [100],
    };

    /// <summary>Plant a ready-made journal volume on disk. All four header fields match by default; break them one at a time with named arguments.</summary>
    private async Task<string> PlantAsync(
        string runId, int configId = ConfigId, int baseline = Baseline, string localRoot = LocalRoot,
        string identity = Identity, string path = "a.bin")
    {
        await using (var journal = await _store.CreateAsync(AccountId, Container, runId, new JournalHeader
        {
            RunId = runId,
            ConfigId = configId,
            StartedAt = DateTimeOffset.UtcNow,
            BaselineVersion = baseline,
            LocalRoot = localRoot,
            EncryptionIdentity = identity,
        }, default))
        {
            await journal.AppendAsync(Blob(path), default);
        }
        return _store.PathFor(AccountId, Container, runId);
    }

    /// <summary>Start a new run and take it through opening the volume. All four terms use the values that match.</summary>
    private async Task<BackupRunControl> OpenAsync(string runId = "run-new", bool firstRun = false)
    {
        var control = new BackupRunControl(_store, ConfigId, runId);
        await control.OpenJournalAsync(
            AccountId, Container, Baseline, LocalRoot, Identity, DateTimeOffset.UtcNow, default, firstRun);
        return control;
    }

    [Fact]
    public async Task Nothing_on_disk_means_nothing_to_resume_and_nothing_to_sweep()
    {
        await using var control = await OpenAsync();

        Assert.True(control.Resume.IsEmpty);
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
        await using var control = await OpenAsync(firstRun: true);

        Assert.True(control.Resume.IsEmpty);
        Assert.True(control.SweepNeeded);
    }

    [Fact]
    public async Task All_four_terms_matching_is_adopted()
    {
        var planted = await PlantAsync("run-old");

        await using var control = await OpenAsync();

        Assert.Equal(1, control.Resume.RecordCount);
        Assert.NotNull(control.Resume.FindBlob("a.bin", "fa.bin", 100, "ha.bin", "ta.bin"));
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

        await using var control = await OpenAsync();

        Assert.True(control.Resume.IsEmpty);
        Assert.False(File.Exists(planted), "a journal belonging to another config must be voided");
        Assert.True(control.SweepNeeded);
    }

    [Fact]
    public async Task A_stale_baseline_version_is_voided()
    {
        // The baseline changed = somebody else already completed a whole run, and the references in that volume should long since be the index's business.
        var planted = await PlantAsync("run-old", baseline: Baseline - 1);

        await using var control = await OpenAsync();

        Assert.True(control.Resume.IsEmpty);
        Assert.False(File.Exists(planted), "a journal from another baseline must be voided");
        Assert.True(control.SweepNeeded);
    }

    [Fact]
    public async Task A_different_local_root_is_voided()
    {
        // Change the root directory and the same relative path no longer means the same file.
        var planted = await PlantAsync("run-old", localRoot: LocalRoot + "-moved");

        await using var control = await OpenAsync();

        Assert.True(control.Resume.IsEmpty);
        Assert.False(File.Exists(planted), "a journal taken under another local root must be voided");
        Assert.True(control.SweepNeeded);
    }

    [Fact]
    public async Task A_different_encryption_identity_is_voided()
    {
        // Change the key and the address space changes with it; not one ref in the old volume still lines up.
        var planted = await PlantAsync("run-old", identity: "keyed:abc");

        await using var control = await OpenAsync();

        Assert.True(control.Resume.IsEmpty);
        Assert.False(File.Exists(planted), "a journal written under another key must be voided");
        Assert.True(control.SweepNeeded);
    }

    [Fact]
    public async Task Every_still_valid_volume_is_adopted_not_just_the_first()
    {
        // Repeated suspend/resume piles up several volumes, and every one of them counts.
        await PlantAsync("run-old-1", path: "a.bin");
        await PlantAsync("run-old-2", path: "b.bin");

        await using var control = await OpenAsync();

        Assert.Equal(2, control.Resume.RecordCount);
        Assert.NotNull(control.Resume.FindBlob("a.bin", "fa.bin", 100, "ha.bin", "ta.bin"));
        Assert.NotNull(control.Resume.FindBlob("b.bin", "fb.bin", 100, "hb.bin", "tb.bin"));
    }

    [Fact]
    public async Task Volumes_are_judged_one_by_one_not_as_a_batch()
    {
        var mine = await PlantAsync("run-old-mine", path: "a.bin");
        var foreign = await PlantAsync("run-old-foreign", configId: ConfigId + 1, path: "b.bin");

        await using var control = await OpenAsync();

        Assert.Equal(1, control.Resume.RecordCount);
        Assert.NotNull(control.Resume.FindBlob("a.bin", "fa.bin", 100, "ha.bin", "ta.bin"));
        Assert.Null(control.Resume.FindBlob("b.bin", "fb.bin", 100, "hb.bin", "tb.bin"));
        Assert.True(File.Exists(mine));
        Assert.False(File.Exists(foreign));
    }

    [Fact]
    public async Task An_adopted_volume_survives_a_run_that_never_commits()
    {
        // Suspended or failed tail (CompleteAsync was never called): the adopted volume must still be there untouched,
        // or the next run can never reuse the blocks it recorded again — and those blocks really are in the cloud.
        var planted = await PlantAsync("run-old");

        var control = await OpenAsync();
        await control.DisposeAsync();

        Assert.True(File.Exists(planted));
        Assert.True(File.Exists(_store.PathFor(AccountId, Container, "run-new")));
    }

    /// <summary>
    /// This run's runId collides with the volume on disk: after adopting it we must **append to it**, not open a fresh volume that overwrites it.
    /// <para>
    /// Today's RunId is a freshly generated GUID prefix, so it cannot collide; Task 15 "automatically carry on at startup" will
    /// reuse the runId of the suspended run (so the run's identity in the UI stays the same), and reusing it collides. After a
    /// truncation the current run is still correct (Resume is already in memory); what breaks is the guarantee on disk — so this case pins **what is left in the file**, not what is in Resume.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_volume_carrying_our_own_run_id_is_appended_to_not_truncated()
    {
        var planted = await PlantAsync("run-same");

        await using (var control = await OpenAsync("run-same"))
        {
            Assert.Equal(1, control.Resume.RecordCount);
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

        var control = await OpenAsync();
        await control.CompleteAsync();
        await control.DisposeAsync();

        Assert.False(File.Exists(planted));
        Assert.False(File.Exists(_store.PathFor(AccountId, Container, "run-new")));
    }
}
