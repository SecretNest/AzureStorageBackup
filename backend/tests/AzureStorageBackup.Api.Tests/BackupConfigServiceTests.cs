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

    // 一个 container 只挂得住一条备份（AppDbContext 上的唯一索引），所以要两条并存的用例
    // 必须各给各的 container——从前它们共用 "photos"，靠的正是这次修掉的那个漏洞。
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
        // 实体里始终是密文；明文只经 ISecretReader 取（设计 §3.1）。
        Assert.NotEqual("s3cret", fetched.PasswordProtected);
        Assert.Equal("s3cret", TestSecrets.Reader.RevealBackupPassword(fetched));
        Assert.Equal(StorageTier.Cool, fetched.DataTier);
        Assert.Equal(RetentionMode.BothRequired, fetched.RetentionMode);
    }

    /// <summary>
    /// 钉住**列名**：实体属性叫 PasswordProtected，落库仍必须是历史列名 Password（无 schema 变更）。
    /// 这条不再证明「加密」——密文是本测试自己经 CreateAsync 写进去的，断言只能说明它不等于明文。
    /// </summary>
    [Fact]
    public async Task Password_Is_Written_To_The_Legacy_Password_Column()
    {
        var created = await _sut.CreateAsync(Sample());

        // 直接读原始列（列名写错就查不到表/列，测试失败）。
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
        update.PasswordProtected = null; // 更新请求不带密码（创建后不可更改，见 Clone 注释）
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
    // 刻意**不**复制 PasswordProtected：密码创建后不可更改（决策 8），更新请求一律不带密码
    // （端点侧 BackupConfigRequest.Password 为空时 PasswordProtected 就是 null）。
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

    // 密码创建后不可更改（决策 8）：密文含随机 IV，「是不是同一个密码」不可比较，
    // 所以只要带了密码就拒绝——无论是新密码，还是原样回传的原密文。
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
        var created = await _sut.CreateAsync(Sample()); // 密码明文 "s3cret"

        var update = Clone(created); // 不带密码 = 保留原值（约定见 BackupConfigEndpoints PUT）
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
        // 范围规则是相对根的坐标，换根后必须原文保留、一字不改。
        created.ScopeRules = "+ albums\n- albums/tmp";
        created.PasswordProtected = null; // 更新请求不带密码（创建后不可更改，见决策 8）
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
    /// 新通道是另开的一道门，不是把旧锁撬开：常规更新路径必须**依然**拒绝改根，
    /// 否则日后一次顺手的编辑就能悄悄换掉根路径。
    /// </summary>
    [Fact]
    public async Task Update_Still_Refuses_To_Change_The_Local_Root()
    {
        var created = await _sut.CreateAsync(Sample());
        var update = await _sut.GetAsync(created.Id);
        update!.LocalRoot = "/mnt/photos";
        update.PasswordProtected = null;   // 空 = 保留原值，避免撞上密码那条拒绝

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.UpdateAsync(created.Id, update));
    }
}
