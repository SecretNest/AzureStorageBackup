using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Extensions and paths do not want the same treatment. `*.mp4` names a kind of file, and a camera writing `.MP4`
/// or a Windows box writing `.WMV` produces the same kind — while on Linux `Temp/` and `temp/` really are two
/// directories, so folding case for everything would quietly widen every path rule ever written.
/// <para>
/// Before this split the whole set was case-sensitive, and a don't-compress list of `*.mp4`, `*.avi`, `*.wmv`
/// silently missed every file a camera had named in upper case: they went through full compression, which for
/// video buys nothing (a real backup measured 47.5 GB uploaded from 48.9 GB of source) and holds the global
/// compression lock the whole time, starving the upload pipeline.
/// </para>
/// </summary>
public class CaseInsensitiveRulesTests
{
    private static bool Hit(IgnoreRuleSet? set, string path) => set?.MatchesFileOrAncestorDir(path) ?? false;

    /// <summary>The half a rule is written in decides how it matches — nothing is inferred from the pattern's shape.</summary>
    [Fact]
    public void Each_half_keeps_its_own_sensitivity()
    {
        var set = BackupRequestMapper.Rules(sensitive: "Temp/", insensitive: "*.mp4");

        // Insensitive half: every casing of the extension is the same kind of file.
        Assert.True(Hit(set, "a.mp4"));
        Assert.True(Hit(set, "a.MP4"));
        Assert.True(Hit(set, "a.Mp4"));

        // Sensitive half: a path is a path. On Linux these are two different directories and must stay so.
        Assert.True(Hit(set, "Temp/x.bin"));
        Assert.False(Hit(set, "temp/x.bin"));
        Assert.False(Hit(set, "TEMP/x.bin"));
    }

    /// <summary>
    /// The reason both halves go into one rule set rather than being consulted separately: gitignore's
    /// "last matching rule decides" has to keep holding across the pair. Two sets OR-ed together would make this
    /// negation silently do nothing.
    /// </summary>
    [Fact]
    public void A_negation_in_one_half_overrides_a_match_in_the_other()
    {
        // The sensitive half sweeps up lower-case mp4s; the insensitive half then carves one name back out,
        // whatever casing it happens to have on disk. This only works because both halves live in one ordered
        // set: the negation is evaluated after the match it is meant to undo.
        var set = BackupRequestMapper.Rules(sensitive: "*.mp4", insensitive: "!keep.mp4");

        Assert.True(Hit(set, "old.mp4"));
        Assert.False(Hit(set, "keep.mp4"));
        Assert.False(Hit(set, "KEEP.MP4"));
    }

    /// <summary>
    /// Directory rules are the one thing a negation cannot undo, and that is gitignore's own rule rather than
    /// anything introduced by the split: once a parent directory is excluded, nothing inside it can be brought
    /// back. Pinned here so the split is not later blamed for it.
    /// </summary>
    [Fact]
    public void A_negation_cannot_re_include_under_an_excluded_directory()
    {
        var set = BackupRequestMapper.Rules(sensitive: "Archive/", insensitive: "!keep.mp4");

        Assert.True(Hit(set, "Archive/old.mp4"));
        Assert.True(Hit(set, "Archive/keep.mp4"));
    }

    /// <summary>Both halves empty means "no rules", which callers distinguish from "rules that match nothing".</summary>
    [Fact]
    public void Nothing_configured_stays_null()
    {
        Assert.Null(BackupRequestMapper.Rules(null, null));
        Assert.Null(BackupRequestMapper.Rules("", "   "));
        Assert.NotNull(BackupRequestMapper.Rules(null, "*.mp4"));
        Assert.NotNull(BackupRequestMapper.Rules("*.mp4", null));
    }

    /// <summary>
    /// The old constructor is still what every other caller uses, and it must not have quietly become
    /// case-insensitive along the way — a path rule that widened itself would be the worst outcome of this change.
    /// </summary>
    [Fact]
    public void The_plain_constructor_is_still_case_sensitive()
    {
        var set = new IgnoreRuleSet(["*.mp4", "Temp/"]);

        Assert.True(Hit(set, "a.mp4"));
        Assert.False(Hit(set, "a.MP4"));
        Assert.True(Hit(set, "Temp/x"));
        Assert.False(Hit(set, "temp/x"));
    }

    /// <summary>
    /// Character classes are not glob syntax here — everything that is not `*` or `?` is regex-escaped, so `[wW]`
    /// matches those three characters literally. Worth pinning down: it is the workaround people reach for first,
    /// and it fails silently by matching nothing.
    /// </summary>
    [Fact]
    public void Character_classes_are_literal_not_syntax()
    {
        var set = new IgnoreRuleSet(["*.[wW][mM][vV]"]);

        Assert.False(Hit(set, "a.wmv"));
        Assert.False(Hit(set, "a.WMV"));
        Assert.True(Hit(set, "a.[wW][mM][vV]"));
    }
}
