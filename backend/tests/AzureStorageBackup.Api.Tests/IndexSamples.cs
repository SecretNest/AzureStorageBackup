using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The one hand-written <see cref="VersionIndex"/> that exercises every branch of the index wire format (a raw-byte
/// multi-volume blob, a symlink, a pack member with a non-xxh128 hash and an unreadable timestamp, an entry with no
/// storage at all, empty dirs, unrecoverable paths). Shared rather than copied, because both the stream round-trip
/// tests and the catalog's byte-identity test have to be talking about the *same* bytes for either result to mean
/// anything: if one copy quietly loses the symlink, the test that still has it is the only one still testing it.
/// </summary>
internal static class IndexSamples
{
    public static VersionIndex Sample() => new()
    {
        Version = 7,
        Entries =
        [
            new IndexEntry { Path = "a/b.txt", Kind = "file", Length = 12, Mtime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8)),
                Permissions = "0644", HeadHash = "xxh128:" + new string('a', 32), TailHash = "xxh128:" + new string('b', 32),
                FullHash = "xxh128:" + new string('c', 32), Storage = new StorageRef { Kind = "blob", Ref = "data/abc", Volumes = 2, Raw = true, VolumeSizes = [10, 2] } },
            new IndexEntry { Path = "a/link", Kind = "symlink", Length = 0, Mtime = DateTimeOffset.UnixEpoch, Permissions = "0777", Target = "../x" },
            new IndexEntry { Path = "gone.txt", Kind = "file", Length = 3, Mtime = DateTimeOffset.UnixEpoch, Permissions = "0600",
                HeadHash = "sha256:notxxh", UnreadableAt = new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero),
                Storage = new StorageRef { Kind = "pack", Ref = "p0001", EntryName = "gone.txt" } },
            new IndexEntry { Path = "empty", Kind = "file", Length = 0, Mtime = DateTimeOffset.UnixEpoch, Permissions = "0644" },
        ],
        EmptyDirs = ["a/empty", "z"],
        UnrecoverablePaths = ["gone.txt"],
    };
}
