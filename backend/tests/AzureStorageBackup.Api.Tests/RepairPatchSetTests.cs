using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The merge rules of <see cref="RepairPatchSet"/>. They decide two things the rest of a repair cannot recover from:
/// which versions get their index rewritten at all, and — because a version's unrecoverable list is written out in
/// list order and that order is part of the index's bytes — in what order the marks land.
/// </summary>
public sealed class RepairPatchSetTests
{
    private static StorageRef Blob(int volumes) =>
        new() { Kind = "blob", Ref = "data/x", Volumes = volumes, VolumeSizes = [1, 2] };

    /// <summary>Several operations on one path collapse into ONE patch: SerializeVersionAsync indexes the patches by
    /// path, so a second patch for the same path would be dropped or, worse, silently win over the first.</summary>
    [Fact]
    public void Operations_on_one_path_merge_into_one_patch()
    {
        var patches = new RepairPatchSet();
        patches.SetStorage(1, "a.txt", Blob(3));
        patches.Clear(1, "a.txt");

        var patch = Assert.Single(patches.PatchesFor(1));
        Assert.Equal("a.txt", patch.Path);
        Assert.False(patch.Unrecoverable);
        Assert.Equal(3, patch.Storage!.Volumes);
    }

    /// <summary>A mark this run raised and has not persisted yet is WITHDRAWN by a clear, not turned into one: the
    /// catalog never heard of it, so a patch saying "clear a mark nobody holds" would rewrite that version's whole
    /// index for a change that cancels itself out.</summary>
    [Fact]
    public void Clearing_a_mark_this_run_raised_leaves_the_version_unchanged()
    {
        var patches = new RepairPatchSet();
        patches.Mark(2, "a.txt");
        Assert.Equal([2], patches.ChangedVersions);

        patches.Clear(2, "a.txt");

        Assert.Empty(patches.ChangedVersions);
        Assert.Empty(patches.PatchesFor(2));
        Assert.Null(patches.IsMarkedPending(2, "a.txt"));   // no opinion — the catalog answers
    }

    /// <summary>The withdrawal is of the mark, not of the whole patch: a repaired entry's new volume sizes are a
    /// change in their own right and must still be written.</summary>
    [Fact]
    public void Withdrawing_a_mark_keeps_a_storage_rewrite_on_the_same_path()
    {
        var patches = new RepairPatchSet();
        patches.Mark(1, "a.txt");
        patches.SetStorage(1, "a.txt", Blob(2));
        patches.Clear(1, "a.txt");

        var patch = Assert.Single(patches.PatchesFor(1));
        Assert.Null(patch.Unrecoverable);
        Assert.Equal(2, patch.Storage!.Volumes);
    }

    /// <summary>A clear where this run holds no mark of its own is a real clear: the mark it overturns is the
    /// catalog's, and only a patch takes it off the record.</summary>
    [Fact]
    public void Clearing_without_a_pending_mark_records_the_clear()
    {
        var patches = new RepairPatchSet();
        patches.Clear(1, "a.txt");

        Assert.False(Assert.Single(patches.PatchesFor(1)).Unrecoverable);
        Assert.False(patches.IsMarkedPending(1, "a.txt"));
    }

    /// <summary>Patches come out in the order their paths were first touched — including a path that was withdrawn
    /// and raised again, which keeps its original slot rather than jumping to the end.</summary>
    [Fact]
    public void Patches_keep_their_first_touch_order()
    {
        var patches = new RepairPatchSet();
        patches.Mark(1, "b.txt");
        patches.Mark(1, "a.txt");
        patches.Mark(1, "c.txt");
        patches.Clear(1, "b.txt");
        patches.Mark(1, "b.txt");

        Assert.Equal(["b.txt", "a.txt", "c.txt"], patches.PatchesFor(1).Select(p => p.Path));
        Assert.All(patches.PatchesFor(1), p => Assert.True(p.Unrecoverable));
    }

    /// <summary>Versions are independent, and Forget drops exactly the one whose patches are now in the cloud AND in
    /// the catalog — keeping them would rewrite that index again at the next persist.</summary>
    [Fact]
    public void Forget_drops_one_version_and_leaves_the_others()
    {
        var patches = new RepairPatchSet();
        patches.Mark(1, "a.txt");
        patches.Mark(2, "a.txt");

        patches.Forget(1);

        Assert.Equal([2], patches.ChangedVersions);
        Assert.Empty(patches.PatchesFor(1));
        Assert.Null(patches.IsMarkedPending(1, "a.txt"));
        Assert.True(patches.IsMarkedPending(2, "a.txt"));
    }
}
