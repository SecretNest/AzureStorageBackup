using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;
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

        var encryption = new EncryptionService(new EphemeralDataProtectionProvider());
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options, encryption);
        _db.Database.EnsureCreated();

        _sut = new BackupConfigService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static BackupConfig Sample(string name = "photos") => new()
    {
        AccountId = 1,
        ContainerName = "photos",
        Name = name,
        LocalRoot = "/data/photos",
        Password = "s3cret",
        DataTier = StorageTier.Cool,
        MaxVersions = 50,
        RetentionMode = RetentionMode.BothRequired,
    };

    [Fact]
    public async Task Create_Then_Get_RoundTrips_Including_Password()
    {
        var created = await _sut.CreateAsync(Sample());

        Assert.True(created.Id > 0);
        Assert.NotEqual(default, created.CreatedAt);

        var fetched = await _sut.GetAsync(created.Id);
        Assert.Equal("photos", fetched!.Name);
        Assert.Equal("s3cret", fetched.Password); // 透明解密
        Assert.Equal(StorageTier.Cool, fetched.DataTier);
        Assert.Equal(RetentionMode.BothRequired, fetched.RetentionMode);
    }

    [Fact]
    public async Task Password_Is_Encrypted_At_Rest()
    {
        var created = await _sut.CreateAsync(Sample());

        // 直接读原始列，应为密文而非明文。
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
        await _sut.CreateAsync(Sample("a"));
        await _sut.CreateAsync(Sample("b"));

        Assert.Equal(2, (await _sut.ListAsync()).Count);
    }

    [Fact]
    public async Task Update_Changes_Fields()
    {
        var created = await _sut.CreateAsync(Sample());

        var update = Sample();
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

    // BackupConfig 是 class（非 record），用逐字段克隆代替 `with` 表达式来构造更新请求。
    private static BackupConfig Clone(BackupConfig c) => new()
    {
        Id = c.Id,
        AccountId = c.AccountId,
        ContainerName = c.ContainerName,
        Name = c.Name,
        Description = c.Description,
        LocalRoot = c.LocalRoot,
        Password = c.Password,
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

        var changePassword = Clone(created);
        changePassword.Password = "different-secret";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.UpdateAsync(created.Id, changePassword));

        // Name/规则/保留策略等可变字段可以正常更新。
        var ok = Clone(created);
        ok.Name = "renamed";
        ok.IgnoreRules = "*.tmp";
        ok.MaxVersions = 7;
        var result = await _sut.UpdateAsync(created.Id, ok);
        Assert.Equal("renamed", result!.Name);
        Assert.Equal("*.tmp", result.IgnoreRules);
        Assert.Equal(7, result.MaxVersions);
    }

    [Fact]
    public async Task Update_Empty_Password_Preserves_Existing_Without_Rejecting()
    {
        var created = await _sut.CreateAsync(Sample()); // Password = "s3cret"

        var update = Clone(created);
        update.Password = null; // 空密码 = 保留原值（约定见 BackupConfigEndpoints PUT），不算基础字段变更
        update.Name = "renamed";

        var result = await _sut.UpdateAsync(created.Id, update);

        Assert.Equal("renamed", result!.Name);
        Assert.Equal("s3cret", (await _sut.GetAsync(created.Id))!.Password);
    }

    [Fact]
    public async Task Delete_Removes_Config()
    {
        var created = await _sut.CreateAsync(Sample());

        Assert.True(await _sut.DeleteAsync(created.Id));
        Assert.Null(await _sut.GetAsync(created.Id));
        Assert.False(await _sut.DeleteAsync(created.Id));
    }
}
