using System.Reflection;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The settings row is one table but two resources on the API (<c>/settings/defaults</c> and
/// <c>/settings/performance</c>), each writing only its own half. That only holds if every field belongs to exactly
/// one half: a field in neither is one the UI can never save; a field in both is the overwrite problem back again.
/// Reflection, so a column added to the row without a home fails here rather than in the field.
/// </summary>
public sealed class SettingsPartitionTests
{
    private static HashSet<string> Names(Type t) =>
        [.. t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)];

    [Fact]
    public void Every_Settings_Field_Belongs_To_Exactly_One_Half()
    {
        var all = Names(typeof(GlobalSettings));
        all.Remove(nameof(GlobalSettings.Id)); // the singleton row's key is nobody's setting
        var defaults = Names(typeof(BackupDefaultsSettings));
        var performance = Names(typeof(PerformanceSettings));

        Assert.Empty(defaults.Intersect(performance));
        Assert.Empty(all.Except(defaults.Union(performance)));
        Assert.Empty(defaults.Union(performance).Except(all)); // no DTO field without a column behind it
    }

    [Fact]
    public void The_Two_Halves_Are_Cut_Where_The_Pages_Are()
    {
        var defaults = Names(typeof(BackupDefaultsSettings));
        var performance = Names(typeof(PerformanceSettings));
        // Defaults for a new backup, and the per-tier repack switches, live on Backup defaults.
        Assert.Contains(nameof(GlobalSettings.DefaultMaxVersions), defaults);
        Assert.Contains(nameof(GlobalSettings.RepackDownloadArchive), defaults);
        // Everything about how the server runs, including the logging pair, lives on Performance.
        Assert.Contains(nameof(GlobalSettings.UploadMemoryLimitBytes), performance);
        Assert.Contains(nameof(GlobalSettings.DefaultVerboseLogging), performance);
        Assert.Contains(nameof(GlobalSettings.LogEphemeralMaxAgeDays), performance);
    }

    [Fact]
    public void A_Half_Applied_To_A_Row_Changes_Only_Its_Own_Fields()
    {
        var row = new GlobalSettings { DefaultMaxVersions = 7, UploadConcurrency = 3 };

        var defaults = BackupDefaultsSettings.From(row) with { DefaultMaxVersions = 9 };
        defaults.ApplyTo(row);
        Assert.Equal(9, row.DefaultMaxVersions);
        Assert.Equal(3, row.UploadConcurrency);

        var performance = PerformanceSettings.From(row) with { UploadConcurrency = 11 };
        performance.ApplyTo(row);
        Assert.Equal(11, row.UploadConcurrency);
        Assert.Equal(9, row.DefaultMaxVersions);
    }
}
