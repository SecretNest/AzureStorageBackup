namespace AzureStorageBackup.Api.Services;

/// <summary>Conflict handling mode during restore (decision 3). Default <see cref="OverwriteIfChanged"/> = the current behavior.</summary>
public enum RestoreConflictMode
{
    /// <summary>Overwrite only when changed: skip if the local file already holds identical content, otherwise overwrite it (current behavior).</summary>
    OverwriteIfChanged = 0,

    /// <summary>Skip as soon as the target exists (whether or not the content differs); write nothing.</summary>
    Skip = 1,

    /// <summary>Target exists with different content → rename the existing local file to {name}.bak-{ts} first, then write the restored content (the old content is never lost).</summary>
    RenameKeep = 2,
}

/// <summary>Rehydrate priority for Archive blobs (passed straight through to Azure's <c>RehydratePriority</c>).</summary>
public enum RestoreRehydratePriority
{
    /// <summary>Standard priority (the default, up to roughly 15 hours).</summary>
    Standard = 0,

    /// <summary>High priority (usually &lt; 1 hour, costs more).</summary>
    High = 1,
}

/// <summary>Restore conflict "rename and keep" (decision 3): rename the existing local file to {name}.bak-{yyyyMMdd-HHmmss}
/// (appending -1/-2… on collision), freeing the original name for the restored content to be written. The old content is never lost.</summary>
public static class RestoreConflict
{
    public static string RenameExisting(string dest, DateTimeOffset now)
    {
        var stamp = now.ToString("yyyyMMdd-HHmmss");
        var baseBak = dest + ".bak-" + stamp;
        var target = baseBak;
        var n = 1;
        while (File.Exists(target) || Directory.Exists(target))
            target = baseBak + "-" + n++;
        File.Move(dest, target);
        return target;
    }
}
