using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The names shown on the in-flight rows. Blobs are content-addressed — under encryption they are HMAC
/// gibberish — so <c>data/9f2a3b7c…001</c> means nothing to the person staring at the screen. The upload side
/// switched to source paths long ago; the download side (restore/verify) has to give the **same shape**, or the
/// two halves of one and the same UI do not read as the same thing.
/// </summary>
public class TransferLabelTests
{
    private static IndexEntry Entry(string path, StorageRef storage) =>
        new() { Path = path, Kind = "file", Permissions = "0644", Length = 100, Storage = storage };

    private static BackupInfoFile Info() =>
        new() { Backup = new BackupMeta { Name = "t", CreatedAt = DateTimeOffset.UtcNow } };

    [Fact]
    public void A_Single_File_Blob_Shows_Its_Source_Path()
    {
        var storage = new StorageRef { Kind = "blob", Ref = "data/9f2a3b7c1e4d8a0b" };
        var label = TransferLabel.For(storage, [Entry("photos/2024/IMG_0042.mov", storage)]);

        Assert.Equal("photos/2024/IMG_0042.mov", label);
    }

    /// <summary>A pack holds hundreds of files, too many to list — so report the pack id and a member count.</summary>
    [Fact]
    public void A_Pack_Shows_Its_Id_And_Member_Count()
    {
        var storage = new StorageRef { Kind = "pack", Ref = "p3f2a9c1e0007" };
        var members = Enumerable.Range(0, 318).Select(i => Entry($"docs/f{i}.txt", storage)).ToList();

        Assert.Equal("pack p3f2a9c1e0007 (318 files)", TransferLabel.For(storage, members));
    }

    [Fact]
    public void One_Member_Is_Not_Pluralised()
    {
        var storage = new StorageRef { Kind = "pack", Ref = "p0001" };
        Assert.Equal("pack p0001 (1 file)", TransferLabel.For(storage, [Entry("a.txt", storage)]));
    }

    /// <summary>
    /// Reports the member count of **this batch**, not of the whole pack. A selective restore only pulls a few of
    /// them, and reporting the full count would clash with the numbers shown elsewhere in the UI.
    /// </summary>
    [Fact]
    public void A_Pack_Counts_Only_The_Members_Being_Handled()
    {
        var storage = new StorageRef { Kind = "pack", Ref = "p0002" };
        var selected = new[] { Entry("a.txt", storage), Entry("b.txt", storage) };

        Assert.Equal("pack p0002 (2 files)", TransferLabel.For(storage, selected));
    }

    /// <summary>Falls back to the ref when no entries are available — a label is no reason to blow up an entire transfer.</summary>
    [Fact]
    public void With_No_Members_It_Falls_Back_To_The_Ref()
    {
        var storage = new StorageRef { Kind = "blob", Ref = "data/abc" };
        Assert.Equal("data/abc", TransferLabel.For(storage, []));
    }

    /// <summary>A single-file blob's volume sizes live right on the entry.</summary>
    [Fact]
    public void Download_Size_Of_A_Blob_Sums_Its_Volumes()
    {
        var storage = new StorageRef { Kind = "blob", Ref = "data/x", VolumeSizes = [100, 200, 50] };
        Assert.Equal(350, TransferLabel.DownloadBytesOf(storage, Info()));
    }

    /// <summary>
    /// A pack's volume sizes live in the **info file** rather than on the entries: dead-weight compaction rewrites
    /// the whole pack, changing volume count and sizes, so entry-level copies would all go stale on every compaction.
    /// </summary>
    [Fact]
    public void Download_Size_Of_A_Pack_Comes_From_The_Info_File()
    {
        var info = Info();
        info.Packs["p0001"] = new PackInfo { Blob = "packs/p0001.7z", VolumeSizes = [1000, 300] };

        var storage = new StorageRef { Kind = "pack", Ref = "p0001", VolumeSizes = [999999] };
        Assert.Equal(1300, TransferLabel.DownloadBytesOf(storage, info));
    }

    /// <summary>Old indexes with no volume sizes report 0 = unknown and the UI shows no size — never take an undersized number as a denominator.</summary>
    [Fact]
    public void An_Unknown_Size_Is_Reported_As_Zero()
    {
        Assert.Equal(0, TransferLabel.DownloadBytesOf(
            new StorageRef { Kind = "pack", Ref = "missing" }, Info()));
        Assert.Equal(0, TransferLabel.DownloadBytesOf(
            new StorageRef { Kind = "blob", Ref = "data/old" }, Info()));
    }

    /// <summary>
    /// The name on the "preparing" row — the one item compressing or extracting. A pack holds hundreds of files and
    /// there is no room to list them, but the pack id is content-addressed gibberish, so what identifies it to a
    /// person is **where its files come from**. Leading slashes because that is how this project already writes a
    /// path relative to the local root everywhere the user types one (see the rule lists).
    /// </summary>
    [Fact]
    public void Folders_Names_The_One_Directory_A_Pack_Came_From()
    {
        var paths = Enumerable.Range(0, 318).Select(i => $"photos/2024/f{i}.jpg");
        Assert.Equal("/photos/2024 — 318 files", TransferLabel.Folders(paths));
    }

    [Fact]
    public void Folders_Does_Not_Pluralise_One_File() =>
        Assert.Equal("/docs — 1 file", TransferLabel.Folders(["docs/a.txt"]));

    /// <summary>Ordered by how many files each contributed, so the two named are the two that actually describe the pack.</summary>
    [Fact]
    public void Folders_Names_The_Two_Biggest_Contributors_First()
    {
        // b contributed 3, c 2, a 1 — so b and c are named and a becomes the remainder.
        string[] paths = ["a/1.txt", "b/1.txt", "b/2.txt", "b/3.txt", "c/1.txt", "c/2.txt"];
        Assert.Equal("/b, /c (+1 more) — 6 files", TransferLabel.Folders(paths));
    }

    /// <summary>Beyond two there is no room, so the rest become a count. Cross-directory packing exists for
    /// hash-sharded trees, where this is the common shape.</summary>
    [Fact]
    public void Folders_Folds_The_Rest_Into_A_Count()
    {
        string[] paths = ["a/1.txt", "b/1.txt", "c/1.txt", "d/1.txt", "e/1.txt"];
        Assert.Equal("/a, /b (+3 more) — 5 files", TransferLabel.Folders(paths));
    }

    /// <summary>
    /// Equal contributors are ordered by name, not by whatever order the members arrived in. Without it the same
    /// pack renders differently between two refreshes of the same row, which reads as the work having moved on.
    /// </summary>
    [Fact]
    public void Folders_Breaks_Ties_By_Name_So_The_Row_Does_Not_Flicker()
    {
        Assert.Equal(
            TransferLabel.Folders(["b/1.txt", "a/1.txt", "c/1.txt"]),
            TransferLabel.Folders(["c/1.txt", "b/1.txt", "a/1.txt"]));
        Assert.Equal("/a, /b (+1 more) — 3 files", TransferLabel.Folders(["c/1.txt", "b/1.txt", "a/1.txt"]));
    }

    /// <summary>A file sitting directly in the local root has no folder above it; the root is written as the bare
    /// separator, the same way the rule lists spell it.</summary>
    [Fact]
    public void Folders_Calls_The_Local_Root_A_Folder_Too() =>
        Assert.Equal("/ — 2 files", TransferLabel.Folders(["a.txt", "b.txt"]));

    /// <summary>Nothing to describe: the caller shows no row rather than an empty one.</summary>
    [Fact]
    public void Folders_Says_Nothing_About_Nothing() =>
        Assert.Equal("", TransferLabel.Folders([]));

    /// <summary>
    /// The constraint that only applies on the restore side, and the one nothing else would catch. A selective
    /// restore extracts a pack whose manifest spans folders the user did not ask for; the row must describe **the
    /// batch being extracted**, never the pack's contents. Passing the already-filtered set is what enforces it —
    /// this case exists so that a later change that reaches for the manifest instead fails here.
    /// </summary>
    [Fact]
    public void Folders_Describes_Only_What_It_Was_Given()
    {
        // The pack holds secrets/ as well; the restore selected photos/ only, so `needed` holds photos/ only.
        string[] needed = ["photos/1.jpg", "photos/2.jpg"];
        var label = TransferLabel.Folders(needed);

        Assert.Equal("/photos — 2 files", label);
        Assert.DoesNotContain("secrets", label);
    }
}
