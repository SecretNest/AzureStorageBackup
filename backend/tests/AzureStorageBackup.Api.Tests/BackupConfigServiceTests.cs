using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

public sealed class BackupConfigServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly BackupConfigService _sut;

    public BackupConfigServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _sut = new BackupConfigService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // A container can hold only one backup (the unique index on AppDbContext), so any case that needs two of them side by side
    // must give each its own container — they used to share "photos", relying on exactly the hole that was fixed this time.
    private static BackupConfig Sample(string name = "photos", string container = "photos") => new()
    {
        AccountId = 1,
        ContainerName = container,
        Name = name,
        LocalRoot = "/data/photos",
        PasswordProtected = TestSecrets.Protect("s3cret"),
        DataTier = StorageTier.Cool,
        MaxVersions = 50,
        RetentionMode = RetentionMode.BothRequired,
    };

    [Fact]
    public async Task Create_Then_Get_Keeps_Password_Ciphertext_That_Reader_Reveals()
    {
        var created = await _sut.CreateAsync(Sample());

        Assert.True(created.Id > 0);
        Assert.NotEqual(default, created.CreatedAt);

        var fetched = await _sut.GetAsync(created.Id);
        Assert.Equal("photos", fetched!.Name);
        // The entity always holds ciphertext; the plaintext is obtained only through ISecretReader (design §3.1).
        Assert.NotEqual("s3cret", fetched.PasswordProtected);
        Assert.Equal("s3cret", TestSecrets.Reader.RevealBackupPassword(fetched));
        Assert.Equal(StorageTier.Cool, fetched.DataTier);
        Assert.Equal(RetentionMode.BothRequired, fetched.RetentionMode);
    }

    /// <summary>
    /// Pins down the **column name**: the entity property is called PasswordProtected, but it must still land in the historical column Password (no schema change).
    /// This no longer proves "encryption" — the ciphertext was written by this very test through CreateAsync, so the assertion can only say it is not equal to the plaintext.
    /// </summary>
    [Fact]
    public async Task Password_Is_Written_To_The_Legacy_Password_Column()
    {
        var created = await _sut.CreateAsync(Sample());

        // Read the raw column directly (get the column name wrong and the table/column is not found, failing the test).
        var raw = _connection.CreateCommand();
        raw.CommandText = "SELECT Password FROM BackupConfigs WHERE Id = $id";
        raw.Parameters.AddWithValue("$id", created.Id);
        var stored = (string?)await raw.ExecuteScalarAsync();

        Assert.NotNull(stored);
        Assert.NotEqual("s3cret", stored);
    }

    [Fact]
    public async Task List_Returns_All()
    {
        await _sut.CreateAsync(Sample("a", "container-a"));
        await _sut.CreateAsync(Sample("b", "container-b"));

        Assert.Equal(2, (await _sut.ListAsync()).Count);
    }

    [Fact]
    public async Task Update_Changes_Fields()
    {
        var created = await _sut.CreateAsync(Sample());

        var update = Sample();
        update.PasswordProtected = null; // update requests carry no password (it cannot be changed after creation, see the Clone comment)
        update.Name = "renamed";
        update.MaxVersions = 10;
        var result = await _sut.UpdateAsync(created.Id, update);

        Assert.Equal("renamed", result!.Name);
        Assert.Equal(10, (await _sut.GetAsync(created.Id))!.MaxVersions);
    }

    [Fact]
    public async Task Update_Missing_Returns_Null()
    {
        Assert.Null(await _sut.UpdateAsync(999, Sample()));
    }

    // BackupConfig is a class (not a record), so update requests are built with a field-by-field clone instead of a `with` expression.
    // PasswordProtected is deliberately **not** copied: the password cannot be changed after creation (decision 8), so update requests never carry one
    // (on the endpoint side, an empty BackupConfigRequest.Password means PasswordProtected is null).
    private static BackupConfig Clone(BackupConfig c) => new()
    {
        Id = c.Id,
        AccountId = c.AccountId,
        ContainerName = c.ContainerName,
        Name = c.Name,
        Description = c.Description,
        LocalRoot = c.LocalRoot,
        IndexTier = c.IndexTier,
        DataTier = c.DataTier,
        IgnoreRules = c.IgnoreRules,
        DontCompressRules = c.DontCompressRules,
        DontGroupRules = c.DontGroupRules,
        IncludeSymlinks = c.IncludeSymlinks,
        MaxVersions = c.MaxVersions,
        MaxAgeDays = c.MaxAgeDays,
        RetentionMode = c.RetentionMode,
        SingleFileThresholdBytes = c.SingleFileThresholdBytes,
        GroupCapBytes = c.GroupCapBytes,
        VolumeBytes = c.VolumeBytes,
        VerboseLogging = c.VerboseLogging,
        ScopeRules = c.ScopeRules,
        CreatedAt = c.CreatedAt,
        Status = c.Status,
        LastError = c.LastError,
        LastErrorAt = c.LastErrorAt,
    };

    [Fact]
    public async Task Update_Rejects_Base_Field_Changes_Allows_Editable_Ones()
    {
        var created = await _sut.CreateAsync(Sample());

        var changeContainer = Clone(created);
        changeContainer.ContainerName = "other-container";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.UpdateAsync(created.Id, changeContainer));

        var changeRoot = Clone(created);
        changeRoot.LocalRoot = "/other";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.UpdateAsync(created.Id, changeRoot));

        var changeAccount = Clone(created);
        changeAccount.AccountId = 2;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.UpdateAsync(created.Id, changeAccount));

        var changeIndexTier = Clone(created);
        changeIndexTier.IndexTier = StorageTier.Cold;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.UpdateAsync(created.Id, changeIndexTier));

        var changeDataTier = Clone(created);
        changeDataTier.DataTier = StorageTier.Hot;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.UpdateAsync(created.Id, changeDataTier));

        // Editable fields such as Name/rules/retention policy update normally.
        var ok = Clone(created);
        ok.Name = "renamed";
        ok.IgnoreRules = "*.tmp";
        ok.MaxVersions = 7;
        var result = await _sut.UpdateAsync(created.Id, ok);
        Assert.Equal("renamed", result!.Name);
        Assert.Equal("*.tmp", result.IgnoreRules);
        Assert.Equal(7, result.MaxVersions);
    }

    // The password cannot be changed after creation (decision 8): the ciphertext carries a random IV, so "is this the same password" cannot be compared,
    // hence any request carrying a password is refused — whether it is a new password or the original ciphertext echoed back.
    [Fact]
    public async Task Update_Rejects_Any_Non_Empty_Password()
    {
        var created = await _sut.CreateAsync(Sample());

        var changePassword = Clone(created);
        changePassword.PasswordProtected = TestSecrets.Protect("different-secret");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.UpdateAsync(created.Id, changePassword));

        var resubmitSameCiphertext = Clone(created);
        resubmitSameCiphertext.PasswordProtected = created.PasswordProtected;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.UpdateAsync(created.Id, resubmitSameCiphertext));
    }

    [Fact]
    public async Task Update_Empty_Password_Preserves_Existing_Without_Rejecting()
    {
        var created = await _sut.CreateAsync(Sample()); // password plaintext "s3cret"

        var update = Clone(created); // no password = keep the existing value (convention documented at BackupConfigEndpoints PUT)
        update.Name = "renamed";

        var result = await _sut.UpdateAsync(created.Id, update);

        Assert.Equal("renamed", result!.Name);
        var fetched = await _sut.GetAsync(created.Id);
        Assert.Equal("s3cret", TestSecrets.Reader.RevealBackupPassword(fetched!));
    }

    [Fact]
    public async Task Update_Can_Change_Scope_Rules()
    {
        var created = await _sut.CreateAsync(Sample());

        var update = Clone(created);
        update.ScopeRules = "-\n+ photos";

        var result = await _sut.UpdateAsync(created.Id, update);

        Assert.Equal("-\n+ photos", result!.ScopeRules);
        Assert.Equal("-\n+ photos", (await _sut.GetAsync(created.Id))!.ScopeRules);
    }

    [Fact]
    public async Task Delete_Removes_Config()
    {
        var created = await _sut.CreateAsync(Sample());

        Assert.True(await _sut.DeleteAsync(created.Id));
        Assert.Null(await _sut.GetAsync(created.Id));
        Assert.False(await _sut.DeleteAsync(created.Id));
    }

    [Fact]
    public async Task ChangeLocalRoot_Moves_The_Root_And_Leaves_Everything_Else_Alone()
    {
        var created = await _sut.CreateAsync(Sample());
        // Scope rules are coordinates relative to the root, so after the move they must be kept verbatim, not one character changed.
        created.ScopeRules = "+ albums\n- albums/tmp";
        created.PasswordProtected = null; // update requests carry no password (it cannot be changed after creation, see decision 8)
        await _sut.UpdateAsync(created.Id, created);
        var before = await _sut.GetAsync(created.Id);

        var moved = await _sut.ChangeLocalRootAsync(created.Id, "/mnt/photos");

        Assert.NotNull(moved);
        Assert.Equal("/mnt/photos", moved!.LocalRoot);

        var after = await _sut.GetAsync(created.Id);
        Assert.Equal("/mnt/photos", after!.LocalRoot);
        Assert.Equal(before!.ScopeRules, after.ScopeRules);
        Assert.Equal(before.AccountId, after.AccountId);
        Assert.Equal(before.ContainerName, after.ContainerName);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Description, after.Description);
        Assert.Equal(before.PasswordProtected, after.PasswordProtected);
        Assert.Equal(before.IndexTier, after.IndexTier);
        Assert.Equal(before.DataTier, after.DataTier);
        Assert.Equal(before.IgnoreRules, after.IgnoreRules);
        Assert.Equal(before.MaxVersions, after.MaxVersions);
        Assert.Equal(before.RetentionMode, after.RetentionMode);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
    }

    [Fact]
    public async Task ChangeLocalRoot_Returns_Null_For_An_Unknown_Config()
    {
        Assert.Null(await _sut.ChangeLocalRootAsync(999999, "/mnt/photos"));
    }

    /// <summary>
    /// The new channel is a separate door, not the old lock pried open: the regular update path must **still** refuse to change the root,
    /// otherwise one casual edit later on could quietly swap the root path out.
    /// </summary>
    [Fact]
    public async Task Update_Still_Refuses_To_Change_The_Local_Root()
    {
        var created = await _sut.CreateAsync(Sample());
        var update = await _sut.GetAsync(created.Id);
        update!.LocalRoot = "/mnt/photos";
        update.PasswordProtected = null;   // empty = keep the existing value, so we do not run into the password refusal

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.UpdateAsync(created.Id, update));
    }
}
