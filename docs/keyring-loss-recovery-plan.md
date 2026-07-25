# 密钥环丢失检测与恢复 —— 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 Data Protection 密钥环丢失时 UI 仍可用，并引导用户逐项重新录入凭据；顺带移除 `ConnectionStrings__AzureStorage` 死代码链。

**Architecture:** 三个敏感字段（账户密钥、代理密码、备份密码）改为**密文原样入库**，移除 EF ValueConverter，解密只发生在两个咽喉处 —— `BlobClientFactory`（所有云操作的唯一入口）与备份密码的统一读取器。新增 `KeyringCanary` 单行表用于启动时判定密钥环健康，`Lost` 状态下拦截调度器与动作端点，并提供带强制验证的重设端点。

**Tech Stack:** .NET 10 / ASP.NET Core Minimal API、EF Core + SQLite、xUnit + NSubstitute + `Microsoft.AspNetCore.Mvc.Testing`、React + TypeScript（Vite）。

设计依据：[keyring-loss-recovery-design.md](keyring-loss-recovery-design.md)。实施前请通读该文件第 1 节的 10 条决策。

## Global Constraints

- 界面文案**一律英文**；代码注释与文档用中文，与现有代码保持一致。
- 数据库列名**不得变化**。属性改名必须配 `HasColumnName` 保持原列名，`KeyringCanary` 建表是本轮唯一允许的 schema 变更。
- 备份密码**不提供更改功能**，也不提供「放弃历史改用新密码」的出口。
- 运行期**零云读**：canary 判定与就绪探针只能读本地，不得访问 Azure。
- 解密失败一律抛 `SecretUnavailableException`，**禁止**回退到空密码或原密文继续执行。
- 全量测试命令：`dotnet test backend/AzureStorageBackup.slnx`。每个任务结束时必须全绿。
- 提交信息用英文，遵循 `type: subject` 格式（参考 `git log`）。

---

### Task 1: 解密失败的类型与 `TryDecrypt`

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/SecretUnavailableException.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/IEncryptionService.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/EncryptionService.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/EncryptionServiceTests.cs`

**Interfaces:**
- Produces: `SecretUnavailableException(string message)`；`IEncryptionService.TryDecrypt(string ciphertext, out string plaintext) -> bool`

- [ ] **Step 1: 写失败的测试**

追加到 `backend/tests/AzureStorageBackup.Api.Tests/EncryptionServiceTests.cs` 的类体内：

```csharp
    [Fact]
    public void TryDecrypt_Returns_True_And_Plaintext_For_Own_Ciphertext()
    {
        var sut = CreateSut();
        var cipher = sut.Encrypt("super-secret-key==");

        var ok = sut.TryDecrypt(cipher, out var plain);

        Assert.True(ok);
        Assert.Equal("super-secret-key==", plain);
    }

    [Fact]
    public void TryDecrypt_Returns_False_When_Keyring_Cannot_Decrypt()
    {
        // 另一个 provider = 另一套密钥环，等价于 /keys 丢失后重新生成
        var written = new EncryptionService(new EphemeralDataProtectionProvider());
        var cipher = written.Encrypt("super-secret-key==");
        var sut = CreateSut();

        var ok = sut.TryDecrypt(cipher, out var plain);

        Assert.False(ok);
        Assert.Equal(string.Empty, plain);
    }

    [Fact]
    public void TryDecrypt_Returns_False_For_Garbage_Input()
    {
        var sut = CreateSut();

        var ok = sut.TryDecrypt("not-a-ciphertext", out _);

        Assert.False(ok);
    }
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~EncryptionServiceTests`
Expected: 编译失败，`'IEncryptionService' does not contain a definition for 'TryDecrypt'`

- [ ] **Step 3: 新建异常类型**

创建 `backend/src/AzureStorageBackup.Api/Services/SecretUnavailableException.cs`：

```csharp
namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 密文无法用当前 Data Protection 密钥环解密（通常是 /keys 丢失或被替换）。
/// 抛出即表示该操作不可继续——禁止回退到空密码或原密文。
/// </summary>
public sealed class SecretUnavailableException(string message) : Exception(message);
```

- [ ] **Step 4: 扩展接口与实现**

`backend/src/AzureStorageBackup.Api/Services/IEncryptionService.cs` 的接口体改为：

```csharp
public interface IEncryptionService
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);

    /// <summary>尝试解密。当前密钥环解不开时返回 false，plaintext 为空串，不抛异常。</summary>
    bool TryDecrypt(string ciphertext, out string plaintext);
}
```

`backend/src/AzureStorageBackup.Api/Services/EncryptionService.cs` 的类体追加：

```csharp
    public bool TryDecrypt(string ciphertext, out string plaintext)
    {
        try
        {
            plaintext = _protector.Unprotect(ciphertext);
            return true;
        }
        catch (Exception)
        {
            // 密钥环换过、密文被截断或根本不是密文——一律视为不可用
            plaintext = string.Empty;
            return false;
        }
    }
```

- [ ] **Step 5: 运行测试，确认通过**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~EncryptionServiceTests`
Expected: PASS，5 passed

- [ ] **Step 6: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/SecretUnavailableException.cs \
        backend/src/AzureStorageBackup.Api/Services/IEncryptionService.cs \
        backend/src/AzureStorageBackup.Api/Services/EncryptionService.cs \
        backend/tests/AzureStorageBackup.Api.Tests/EncryptionServiceTests.cs
git commit -m "feat: add TryDecrypt and SecretUnavailableException"
```

---

### Task 2: `ISecretReader` —— 密文到明文的唯一读取口

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/ISecretReader.cs`
- Create: `backend/src/AzureStorageBackup.Api/Services/SecretReader.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/SecretReaderTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `IEncryptionService.TryDecrypt`、`SecretUnavailableException`
- Produces: `ISecretReader.RevealAccountKey(Account) -> string`、`RevealProxyPassword(Account) -> string?`、`RevealBackupPassword(BackupConfig) -> string?`

本任务在模型改名（Task 3）之前完成，因此暂时读取旧属性名 `AccountKey` / `ProxyPassword` / `Password`；Task 3 会把它们改为 `*Protected`。

- [ ] **Step 1: 写失败的测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/SecretReaderTests.cs`：

```csharp
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;

namespace AzureStorageBackup.Api.Tests;

public class SecretReaderTests
{
    private static (SecretReader Sut, IEncryptionService Enc) Create()
    {
        var enc = new EncryptionService(new EphemeralDataProtectionProvider());
        return (new SecretReader(enc), enc);
    }

    [Fact]
    public void RevealAccountKey_Returns_Plaintext()
    {
        var (sut, enc) = Create();
        var account = new Account { AccountKey = enc.Encrypt("the-key==") };

        Assert.Equal("the-key==", sut.RevealAccountKey(account));
    }

    [Fact]
    public void RevealAccountKey_Throws_When_Undecryptable()
    {
        var (sut, _) = Create();
        var foreign = new EncryptionService(new EphemeralDataProtectionProvider());
        var account = new Account { Id = 7, Name = "prod", AccountKey = foreign.Encrypt("the-key==") };

        var ex = Assert.Throws<SecretUnavailableException>(() => sut.RevealAccountKey(account));
        Assert.Contains("prod", ex.Message);
    }

    [Fact]
    public void RevealProxyPassword_Returns_Null_When_Not_Set()
    {
        var (sut, _) = Create();

        Assert.Null(sut.RevealProxyPassword(new Account { ProxyPassword = null }));
        Assert.Null(sut.RevealProxyPassword(new Account { ProxyPassword = "" }));
    }

    [Fact]
    public void RevealBackupPassword_Returns_Null_For_Unencrypted_Backup()
    {
        var (sut, _) = Create();

        Assert.Null(sut.RevealBackupPassword(new BackupConfig { Password = null }));
        Assert.Null(sut.RevealBackupPassword(new BackupConfig { Password = "" }));
    }

    [Fact]
    public void RevealBackupPassword_Throws_When_Undecryptable()
    {
        var (sut, _) = Create();
        var foreign = new EncryptionService(new EphemeralDataProtectionProvider());
        var config = new BackupConfig { Id = 3, Name = "docs", Password = foreign.Encrypt("pw") };

        var ex = Assert.Throws<SecretUnavailableException>(() => sut.RevealBackupPassword(config));
        Assert.Contains("docs", ex.Message);
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~SecretReaderTests`
Expected: 编译失败，`The type or namespace name 'SecretReader' could not be found`

- [ ] **Step 3: 实现接口与类**

创建 `backend/src/AzureStorageBackup.Api/Services/ISecretReader.cs`：

```csharp
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 敏感字段落库为密文；本接口是密文→明文的**唯一**读取口（设计 §3.1「咽喉处解密」）。
/// 解不开一律抛 <see cref="SecretUnavailableException"/>，不得回退。
/// </summary>
public interface ISecretReader
{
    string RevealAccountKey(Account account);
    string? RevealProxyPassword(Account account);

    /// <summary>备份密码；未加密的备份返回 null。</summary>
    string? RevealBackupPassword(BackupConfig config);
}
```

创建 `backend/src/AzureStorageBackup.Api/Services/SecretReader.cs`：

```csharp
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>无状态，注册为单例（<see cref="IEncryptionService"/> 同为单例）。</summary>
public sealed class SecretReader(IEncryptionService encryption) : ISecretReader
{
    public string RevealAccountKey(Account account) =>
        Reveal(account.AccountKey, $"account '{account.Name}' (id {account.Id}) key")!;

    public string? RevealProxyPassword(Account account) =>
        string.IsNullOrEmpty(account.ProxyPassword)
            ? null
            : Reveal(account.ProxyPassword, $"account '{account.Name}' (id {account.Id}) proxy password");

    public string? RevealBackupPassword(BackupConfig config) =>
        string.IsNullOrEmpty(config.Password)
            ? null
            : Reveal(config.Password, $"backup '{config.Name}' (id {config.Id}) password");

    private string? Reveal(string ciphertext, string what) =>
        encryption.TryDecrypt(ciphertext, out var plain)
            ? plain
            : throw new SecretUnavailableException(
                $"Cannot decrypt {what}: the data protection keyring cannot read it. Re-enter the credential.");
}
```

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~SecretReaderTests`
Expected: PASS，5 passed

- [ ] **Step 5: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/ISecretReader.cs \
        backend/src/AzureStorageBackup.Api/Services/SecretReader.cs \
        backend/tests/AzureStorageBackup.Api.Tests/SecretReaderTests.cs
git commit -m "feat: add ISecretReader as the single decryption chokepoint"
```

---

### Task 3: 切换为密文入库（原子改动）

移除 ValueConverter、属性改名、接上 `ISecretReader`。**这三件事无法拆分**——任何一件单独做都编译不过或语义错误。这是本计划最大的任务，按步骤严格执行。

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Models/Account.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Models/BackupConfig.cs:32`
- Modify: `backend/src/AzureStorageBackup.Api/Data/AppDbContext.cs:11,25-33,40-41,70`
- Modify: `backend/src/AzureStorageBackup.Api/Data/AppDbContextFactory.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BlobClientFactory.cs:24-35,54-80`
- Modify: `backend/src/AzureStorageBackup.Api/Services/IBlobClientFactory.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/AccountService.cs:35,41`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupConfigService.cs:49`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupRequestMapper.cs:87-88`
- Modify: `backend/src/AzureStorageBackup.Api/Services/TaskDispatcher.cs:78`
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/AccountEndpoints.cs:41-46`
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs:98-99,190,215,239,265,361,389`
- Modify: `backend/src/AzureStorageBackup.Api/Models/BackupConfigDtos.cs:39`
- Modify: `backend/src/AzureStorageBackup.Api/Program.cs:24,38`
- Test: 10 个既有测试类中的 `new AppDbContext(options, encryption)`

**Interfaces:**
- Consumes: Task 2 的 `ISecretReader`
- Produces: `Account.AccountKeyProtected`、`Account.ProxyPasswordProtected`、`BackupConfig.PasswordProtected`（均为密文）；`AppDbContext(DbContextOptions<AppDbContext>)` 单参构造

- [ ] **Step 1: 写失败的测试 —— 锁定新契约**

创建 `backend/tests/AzureStorageBackup.Api.Tests/CiphertextAtRestTests.cs`：

```csharp
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>密文入库：EF 层不再解密，列表查询在密钥环丢失时依然可用（设计 §3.1）。</summary>
public class CiphertextAtRestTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public CiphertextAtRestTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Stored_Value_Stays_Ciphertext_And_Reader_Reveals_It()
    {
        var enc = new EncryptionService(new EphemeralDataProtectionProvider());
        _db.Accounts.Add(new Account
        {
            Name = "prod",
            BlobEndpoint = "https://prod.blob.core.windows.net",
            AccountKeyProtected = enc.Encrypt("the-key=="),
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var loaded = await _db.Accounts.SingleAsync();

        Assert.NotEqual("the-key==", loaded.AccountKeyProtected);
        Assert.Equal("the-key==", new SecretReader(enc).RevealAccountKey(loaded));
    }

    [Fact]
    public async Task Listing_Succeeds_When_Keyring_Is_Lost()
    {
        // 用一套密钥环写入，再用另一套读——等价于 /keys 丢失后重启
        var written = new EncryptionService(new EphemeralDataProtectionProvider());
        _db.Accounts.Add(new Account
        {
            Name = "prod",
            BlobEndpoint = "https://prod.blob.core.windows.net",
            AccountKeyProtected = written.Encrypt("the-key=="),
        });
        _db.BackupConfigs.Add(new BackupConfig
        {
            Name = "docs",
            ContainerName = "c",
            LocalRoot = "/data",
            PasswordProtected = written.Encrypt("pw"),
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // 关键回归：这两个查询以前会抛 CryptographicException
        var accounts = await _db.Accounts.AsNoTracking().ToListAsync();
        var configs = await _db.BackupConfigs.AsNoTracking().ToListAsync();

        Assert.Single(accounts);
        Assert.Equal("prod", accounts[0].Name);
        Assert.Single(configs);
        Assert.Equal("docs", configs[0].Name);

        // 但真正取用时必须明确失败
        var reader = new SecretReader(new EncryptionService(new EphemeralDataProtectionProvider()));
        Assert.Throws<SecretUnavailableException>(() => reader.RevealAccountKey(accounts[0]));
    }

    [Fact]
    public async Task Column_Names_Are_Unchanged()
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('Accounts')";
        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));

        Assert.Contains("AccountKey", columns);
        Assert.Contains("ProxyPassword", columns);
        Assert.DoesNotContain("AccountKeyProtected", columns);
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~CiphertextAtRestTests`
Expected: 编译失败，`'Account' does not contain a definition for 'AccountKeyProtected'`

- [ ] **Step 3: 模型属性改名**

`backend/src/AzureStorageBackup.Api/Models/Account.cs`——改注释与两个属性：

```csharp
/// <summary>
/// 一个 Azure Storage Account 配置。敏感字段（AccountKeyProtected、ProxyPasswordProtected）
/// 在应用层与库中**均为密文**，解密只经 ISecretReader（设计 §3.1）。
/// </summary>
```

```csharp
    /// <summary>账户密钥密文。取明文用 ISecretReader.RevealAccountKey。</summary>
    public string AccountKeyProtected { get; set; } = string.Empty;
```

```csharp
    /// <summary>代理密码密文。取明文用 ISecretReader.RevealProxyPassword。</summary>
    public string? ProxyPasswordProtected { get; set; }
```

`backend/src/AzureStorageBackup.Api/Models/BackupConfig.cs:32` 改为：

```csharp
    /// <summary>加密密码密文。空 = 不加密。取明文用 ISecretReader.RevealBackupPassword。</summary>
    public string? PasswordProtected { get; set; }
```

- [ ] **Step 4: 移除 ValueConverter，固定列名**

`backend/src/AzureStorageBackup.Api/Data/AppDbContext.cs`——删除 `using Microsoft.EntityFrameworkCore.Storage.ValueConversion;`，构造函数去掉 `IEncryptionService`：

```csharp
/// <summary>
/// 应用数据上下文（SQLite）。敏感字段在库与实体中均为密文，不在此处加解密（设计 §3.1）。
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
```

删除 `OnModelCreating` 开头的两个 `ValueConverter` 声明（原 `:26-33`），并把三处映射改为：

```csharp
            entity.Property(e => e.AccountKeyProtected).IsRequired().HasColumnName("AccountKey");
            entity.Property(e => e.ProxyPasswordProtected).HasColumnName("ProxyPassword");
```

```csharp
            e.Property(x => x.PasswordProtected).HasColumnName("Password"); // 密文落库，列名不变
```

- [ ] **Step 5: 修正设计时工厂**

`backend/src/AzureStorageBackup.Api/Data/AppDbContextFactory.cs` 整体替换为：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AzureStorageBackup.Api.Data;

/// <summary>
/// 设计时（`dotnet ef migrations`）构造 <see cref="AppDbContext"/>：仅用于生成迁移的 schema，不跑 Web 主机。
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=ef-design.db")
            .Options;
        return new AppDbContext(options);
    }
}
```

- [ ] **Step 6: 咽喉一 —— `BlobClientFactory` 解密**

`backend/src/AzureStorageBackup.Api/Services/BlobClientFactory.cs`：类声明改为注入 `ISecretReader`，并把两处消费点改掉。

```csharp
public class BlobClientFactory(ISecretReader secrets) : IBlobClientFactory
```

`:28` 附近改为：

```csharp
        var credential = new StorageSharedKeyCredential(accountName, secrets.RevealAccountKey(account));
```

`CreateProxyHandler` 原为 `public static`，现需读取密码，改为实例方法（`:54`）：

```csharp
    /// <summary>根据账户代理设置构造 HttpClientHandler（公开以便单元测试）。</summary>
    public HttpClientHandler CreateProxyHandler(Account account)
```

其内 `:74` 改为：

```csharp
                proxy.Credentials = new NetworkCredential(
                    account.ProxyUsername, secrets.RevealProxyPassword(account));
```

若 `IBlobClientFactory` 未声明 `CreateProxyHandler`，保持不变；`BlobClientFactoryTests.cs` 中对该方法的静态调用需改为实例调用 `new BlobClientFactory(new SecretReader(enc)).CreateProxyHandler(account)`。

- [ ] **Step 7: 咽喉二 —— 备份密码统一读取**

`backend/src/AzureStorageBackup.Api/Services/BackupRequestMapper.cs:87-88` 删除静态 `Password` 方法（它无法访问 `ISecretReader`）：

```csharp
    // 备份密码改由 ISecretReader.RevealBackupPassword 提供（设计 §3.1），此处不再暴露。
```

`backend/src/AzureStorageBackup.Api/Services/TaskDispatcher.cs:78` 改为使用注入的 `ISecretReader`（在其构造参数中加入 `ISecretReader secrets`）：

```csharp
        var password = secrets.RevealBackupPassword(config);
```

`backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs` 的六处 `var password = string.IsNullOrEmpty(config.Password) ? null : config.Password;`（`:190,215,239,265,361,389`）统一改为：

```csharp
            var password = secrets.RevealBackupPassword(config);
```

并在这六个 handler 的参数列表中加入 `ISecretReader secrets`。

- [ ] **Step 8: 搬运点跟随改名**

- `backend/src/AzureStorageBackup.Api/Services/AccountService.cs:35,41` → `existing.AccountKeyProtected = update.AccountKeyProtected;` / `existing.ProxyPasswordProtected = update.ProxyPasswordProtected;`
- `backend/src/AzureStorageBackup.Api/Endpoints/AccountEndpoints.cs:43-46` → 判断 `req.AccountKey` 不变（请求体仍是明文），赋值改为 `update.AccountKeyProtected = existing.AccountKeyProtected;` / `update.ProxyPasswordProtected = existing.ProxyPasswordProtected;`
- `backend/src/AzureStorageBackup.Api/Models/AccountDtos.cs:36` 的 `AccountRequest.ToAccount()` 需把明文加密后写入 `AccountKeyProtected`。签名改为 `public Account ToAccount(IEncryptionService encryption)`，其中：

```csharp
        AccountKeyProtected = string.IsNullOrEmpty(AccountKey) ? string.Empty : encryption.Encrypt(AccountKey),
        ProxyPasswordProtected = string.IsNullOrEmpty(ProxyPassword) ? null : encryption.Encrypt(ProxyPassword),
```

  三个调用处 `AccountEndpoints.cs:31,41,61` 相应传入注入的 `IEncryptionService encryption`（在各 handler 参数列表中加入）。空值保持空值，不加密。
- `backend/src/AzureStorageBackup.Api/Models/BackupConfigDtos.cs:39` → `!string.IsNullOrEmpty(c.PasswordProtected)`（密文非空 ⟺ 明文非空，语义不变）
- `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs:729` 等使用 `request.Password` 之处**不改**——请求对象里流动的仍是明文。

- [ ] **Step 9: 修正备份密码不可更改的判定**

`backend/src/AzureStorageBackup.Api/Services/BackupConfigService.cs:47-50` 整体替换（原逻辑比较两个值，密文含随机 IV 不可比较，见设计 §3.1 第 2 点）：

```csharp
        // 密码创建后不可更改（设计决策 8）。空 = 保留原值；非空一律拒绝，重设走专用端点。
        if (!string.IsNullOrEmpty(update.PasswordProtected))
            throw new InvalidOperationException("Password cannot be changed after creation; leave it empty.");
```

- [ ] **Step 10: DI 注册调整**

`backend/src/AzureStorageBackup.Api/Program.cs:24` 改为单例并注册读取器（`BlobClientFactory` 是单例，注入 Scoped 会在启动时抛作用域校验异常）：

```csharp
builder.Services.AddSingleton<IEncryptionService, EncryptionService>();
builder.Services.AddSingleton<ISecretReader, SecretReader>();
```

- [ ] **Step 11: 修正 10 个既有测试的 `AppDbContext` 构造**

以下文件中的 `new AppDbContext(options, encryption)` 改为 `new AppDbContext(options)`，并删除随之无用的 `encryption` 局部变量与 `Microsoft.AspNetCore.DataProtection` using（若不再使用）：

```bash
grep -rln "new AppDbContext(" backend/tests --include=*.cs
```

测试中对 `AccountKey` / `Password` 的**写入**需改为写密文，例如 `AccountServiceTests` 的 `SampleAccount()`：

```csharp
        AccountKeyProtected = new EncryptionService(new EphemeralDataProtectionProvider()).Encrypt("the-secret-key=="),
```

若某测试断言「落库后再读出等于原明文」，改为断言「读出的是密文，且经同一 `SecretReader` 可还原」。

- [ ] **Step 12: 全量测试**

Run: `dotnet test backend/AzureStorageBackup.slnx`
Expected: 全绿。若 `BackupConfigServiceTests` 中存在「提交相同密码应放行」的用例，按 Step 9 的新契约改为断言抛 `InvalidOperationException`。

- [ ] **Step 13: 提交**

```bash
git add -A backend/
git commit -m "refactor: store secrets as ciphertext, decrypt only at chokepoints"
```

---

### Task 4: `KeyringCanary` 表与启动判定

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Models/KeyringCanary.cs`
- Create: `backend/src/AzureStorageBackup.Api/Services/IKeyringHealth.cs`
- Create: `backend/src/AzureStorageBackup.Api/Services/KeyringHealth.cs`
- Create: `backend/src/AzureStorageBackup.Api/Services/KeyringProbe.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Data/AppDbContext.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Program.cs:152-157`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/KeyringProbeTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `TryDecrypt`；Task 3 的 `AccountKeyProtected` / `PasswordProtected`
- Produces: `KeyringStatus { Healthy, Lost }`；`IKeyringHealth.Status` / `IKeyringHealth.Set(KeyringStatus)`；`KeyringProbe.EvaluateAsync(CancellationToken) -> Task<KeyringStatus>`；`KeyringProbe.WriteCanaryAsync(CancellationToken)`；`KeyringProbe.CanaryPlaintext`

- [ ] **Step 1: 写失败的测试 —— 四个判定分支**

创建 `backend/tests/AzureStorageBackup.Api.Tests/KeyringProbeTests.cs`：

```csharp
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

public class KeyringProbeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public KeyringProbeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static IEncryptionService NewKeyring() =>
        new EncryptionService(new EphemeralDataProtectionProvider());

    private Account AddAccount(IEncryptionService enc, int id, string name) =>
        new() { Id = id, Name = name, BlobEndpoint = "https://x.blob.core.windows.net", AccountKeyProtected = enc.Encrypt("k") };

    [Fact]
    public async Task Fresh_Database_Is_Healthy_And_Writes_Canary()
    {
        var sut = new KeyringProbe(_db, NewKeyring());

        Assert.Equal(KeyringStatus.Healthy, await sut.EvaluateAsync());
        Assert.Equal(1, await _db.KeyringCanaries.CountAsync());
    }

    [Fact]
    public async Task Existing_Canary_That_Decrypts_Is_Healthy()
    {
        var enc = NewKeyring();
        await new KeyringProbe(_db, enc).EvaluateAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(KeyringStatus.Healthy, await new KeyringProbe(_db, enc).EvaluateAsync());
    }

    [Fact]
    public async Task Existing_Canary_That_Fails_To_Decrypt_Is_Lost()
    {
        await new KeyringProbe(_db, NewKeyring()).EvaluateAsync();
        _db.ChangeTracker.Clear();

        // 新密钥环 = /keys 丢失后重新生成
        Assert.Equal(KeyringStatus.Lost, await new KeyringProbe(_db, NewKeyring()).EvaluateAsync());
    }

    [Fact]
    public async Task Legacy_Database_With_Readable_Secret_Is_Healthy_And_Backfills_Canary()
    {
        var enc = NewKeyring();
        _db.Accounts.Add(AddAccount(enc, 1, "prod"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(KeyringStatus.Healthy, await new KeyringProbe(_db, enc).EvaluateAsync());
        Assert.Equal(1, await _db.KeyringCanaries.CountAsync());
    }

    [Fact]
    public async Task Legacy_Database_With_Unreadable_Secret_Is_Lost_And_Writes_No_Canary()
    {
        _db.Accounts.Add(AddAccount(NewKeyring(), 1, "prod"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(KeyringStatus.Lost, await new KeyringProbe(_db, NewKeyring()).EvaluateAsync());
        Assert.Equal(0, await _db.KeyringCanaries.CountAsync());
    }

    [Fact]
    public async Task Probe_Uses_Lowest_Id_Account_Deterministically()
    {
        var good = NewKeyring();
        // Id 2 用另一套密钥环写入；判定必须只看 Id 最小的那条（Id 1），故为 Healthy
        _db.Accounts.Add(AddAccount(good, 1, "first"));
        _db.Accounts.Add(AddAccount(NewKeyring(), 2, "second"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(KeyringStatus.Healthy, await new KeyringProbe(_db, good).EvaluateAsync());
    }

    [Fact]
    public async Task Falls_Back_To_Backup_Config_When_No_Account_Exists()
    {
        _db.BackupConfigs.Add(new BackupConfig
        {
            Id = 1, Name = "docs", ContainerName = "c", LocalRoot = "/data",
            PasswordProtected = NewKeyring().Encrypt("pw"),
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(KeyringStatus.Lost, await new KeyringProbe(_db, NewKeyring()).EvaluateAsync());
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~KeyringProbeTests`
Expected: 编译失败，`The type or namespace name 'KeyringProbe' could not be found`

- [ ] **Step 3: 新建实体与状态服务**

创建 `backend/src/AzureStorageBackup.Api/Models/KeyringCanary.cs`：

```csharp
namespace AzureStorageBackup.Api.Models;

/// <summary>
/// 密钥环健康哨兵（单行）。存已知常量明文的密文，**不经任何 ValueConverter**，
/// 由 KeyringProbe 显式 Protect/Unprotect（设计 §3.2）。
/// </summary>
public class KeyringCanary
{
    public int Id { get; set; }
    public string Ciphertext { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
```

创建 `backend/src/AzureStorageBackup.Api/Services/IKeyringHealth.cs`：

```csharp
namespace AzureStorageBackup.Api.Services;

public enum KeyringStatus
{
    Healthy = 0,
    Lost = 1,
}

/// <summary>进程级密钥环状态。启动时判定一次并缓存；重设流程完成时显式翻转（设计 §3.2）。</summary>
public interface IKeyringHealth
{
    KeyringStatus Status { get; }
    void Set(KeyringStatus status);
}
```

创建 `backend/src/AzureStorageBackup.Api/Services/KeyringHealth.cs`：

```csharp
namespace AzureStorageBackup.Api.Services;

/// <summary>单例。写入极少（启动一次 + 恢复完成一次），读取频繁，用 volatile 字段即可。</summary>
public sealed class KeyringHealth : IKeyringHealth
{
    private volatile int _status = (int)KeyringStatus.Healthy;

    public KeyringStatus Status => (KeyringStatus)_status;

    public void Set(KeyringStatus status) => _status = (int)status;
}
```

- [ ] **Step 4: 实现判定逻辑**

创建 `backend/src/AzureStorageBackup.Api/Services/KeyringProbe.cs`：

```csharp
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>密钥环健康判定（设计 §3.2 的四分支表）。Scoped——持有 AppDbContext。</summary>
public sealed class KeyringProbe(AppDbContext db, IEncryptionService encryption)
{
    public const string CanaryPlaintext = "canary.v1";

    public async Task<KeyringStatus> EvaluateAsync(CancellationToken ct = default)
    {
        var canary = await db.KeyringCanaries.AsNoTracking().OrderBy(c => c.Id).FirstOrDefaultAsync(ct);
        if (canary is not null)
            return encryption.TryDecrypt(canary.Ciphertext, out var value) && value == CanaryPlaintext
                ? KeyringStatus.Healthy
                : KeyringStatus.Lost;

        // 无 canary 行：可能是升级上来的老库，也可能是全新库。
        // 必须先拿现存密文探一次——否则「升级时密钥环已丢失」会被漏检且此后永远检测不出。
        var probe = await db.Accounts.AsNoTracking().OrderBy(a => a.Id)
            .Select(a => a.AccountKeyProtected).FirstOrDefaultAsync(ct);

        if (string.IsNullOrEmpty(probe))
            probe = await db.BackupConfigs.AsNoTracking().OrderBy(c => c.Id)
                .Where(c => c.PasswordProtected != null && c.PasswordProtected != "")
                .Select(c => c.PasswordProtected).FirstOrDefaultAsync(ct);

        if (!string.IsNullOrEmpty(probe) && !encryption.TryDecrypt(probe, out _))
            return KeyringStatus.Lost;

        await WriteCanaryAsync(ct);
        return KeyringStatus.Healthy;
    }

    /// <summary>写入（或重建）哨兵。恢复流程全部完成后调用。</summary>
    public async Task WriteCanaryAsync(CancellationToken ct = default)
    {
        var existing = await db.KeyringCanaries.ToListAsync(ct);
        if (existing.Count > 0)
            db.KeyringCanaries.RemoveRange(existing);

        db.KeyringCanaries.Add(new KeyringCanary
        {
            Ciphertext = encryption.Encrypt(CanaryPlaintext),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
```

`backend/src/AzureStorageBackup.Api/Data/AppDbContext.cs` 加 DbSet 与键：

```csharp
    public DbSet<KeyringCanary> KeyringCanaries => Set<KeyringCanary>();
```

在 `OnModelCreating` 中加：

```csharp
        modelBuilder.Entity<KeyringCanary>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Ciphertext).IsRequired();
        });
```

- [ ] **Step 5: 运行测试，确认通过**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~KeyringProbeTests`
Expected: PASS，7 passed

- [ ] **Step 6: 生成迁移**

```bash
dotnet ef migrations add AddKeyringCanary --project backend/src/AzureStorageBackup.Api
```

打开新生成的 `backend/src/AzureStorageBackup.Api/Migrations/*_AddKeyringCanary.cs`，确认它**只**包含 `CreateTable("KeyringCanaries", ...)`。若出现任何对 `Accounts.AccountKey` / `BackupConfigs.Password` 的 `RenameColumn` 或 `AlterColumn`，说明 Step 4 的 `HasColumnName` 漏了——回去补上并重新生成。

- [ ] **Step 7: 接入启动流程**

`backend/src/AzureStorageBackup.Api/Program.cs`——在 DI 区注册：

```csharp
builder.Services.AddSingleton<IKeyringHealth, KeyringHealth>();
builder.Services.AddScoped<KeyringProbe>();
```

把 `:152-157` 的迁移块改为：

```csharp
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    // 密钥环健康判定（设计 §3.2）：纯本地，不访问云端。
    var status = await scope.ServiceProvider.GetRequiredService<KeyringProbe>().EvaluateAsync();
    app.Services.GetRequiredService<IKeyringHealth>().Set(status);
    if (status == KeyringStatus.Lost)
        app.Services.GetRequiredService<ILogger<Program>>().LogError(
            "Data protection keyring cannot decrypt stored secrets; entering recovery mode.");
}
```

- [ ] **Step 8: 全量测试并提交**

Run: `dotnet test backend/AzureStorageBackup.slnx`
Expected: 全绿

```bash
git add -A backend/
git commit -m "feat: detect data protection keyring loss with a canary probe"
```

---

### Task 5: 恢复模式闸门

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/SchedulerService.cs`
- Create: `backend/src/AzureStorageBackup.Api/Endpoints/KeyringGuard.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs`（六个动作 handler）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/KeyringGuardTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `IKeyringHealth`
- Produces: `KeyringGuard.Blocked(IKeyringHealth) -> IResult?`（`Lost` 时返回 409，否则 null）

- [ ] **Step 1: 写失败的测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/KeyringGuardTests.cs`：

```csharp
using AzureStorageBackup.Api.Endpoints;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class KeyringGuardTests
{
    private sealed class FixedHealth(KeyringStatus status) : IKeyringHealth
    {
        public KeyringStatus Status { get; private set; } = status;
        public void Set(KeyringStatus s) => Status = s;
    }

    [Fact]
    public void Returns_Null_When_Healthy()
        => Assert.Null(KeyringGuard.Blocked(new FixedHealth(KeyringStatus.Healthy)));

    [Fact]
    public void Returns_Conflict_When_Lost()
    {
        var result = KeyringGuard.Blocked(new FixedHealth(KeyringStatus.Lost));

        Assert.NotNull(result);
        var statusCode = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(409, statusCode.StatusCode);
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~KeyringGuardTests`
Expected: 编译失败，`The type or namespace name 'KeyringGuard' could not be found`

- [ ] **Step 3: 实现闸门**

创建 `backend/src/AzureStorageBackup.Api/Endpoints/KeyringGuard.cs`：

```csharp
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>
/// 恢复模式闸门（设计 §3.3）：密钥环丢失时，所有需要凭据的动作在入口即 409 失败，
/// 不进入编排层——避免用解不开的密码发起云操作。
/// </summary>
public static class KeyringGuard
{
    public static IResult? Blocked(IKeyringHealth health) =>
        health.Status == KeyringStatus.Lost
            ? Results.Json(
                new { error = "Data protection keys were lost; re-enter credentials before running this action.", code = "keyring_lost" },
                statusCode: StatusCodes.Status409Conflict)
            : null;
}
```

- [ ] **Step 4: 接到动作端点**

`backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs` 中，Task 3 Step 7 改过的六个 handler（备份、还原、检查、修复、清理、文件版本查询）各自在参数列表加入 `IKeyringHealth keyring`，并在方法体**第一行**加：

```csharp
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;
```

- [ ] **Step 5: 调度器跳过**

`backend/src/AzureStorageBackup.Api/Services/SchedulerService.cs`——构造参数加入 `IKeyringHealth keyring`，`TickAsync` 开头加：

```csharp
        if (keyring.Status == KeyringStatus.Lost)
        {
            // 每 tick 只记一条汇总，不逐任务记——否则日志会被刷爆（设计 §3.3）
            logger.LogWarning("Keyring lost; skipping all scheduled tasks until credentials are re-entered.");
            return;
        }
```

- [ ] **Step 6: 全量测试并提交**

Run: `dotnet test backend/AzureStorageBackup.slnx`
Expected: 全绿

```bash
git add -A backend/
git commit -m "feat: block credential-dependent actions while the keyring is lost"
```

---

### Task 6: 状态与待重设清单的读取接口

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/SystemEndpoints.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Models/AccountDtos.cs:4`（`AccountResponse` 与其 `From`）
- Modify: `backend/src/AzureStorageBackup.Api/Models/BackupConfigDtos.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/KeyringStatusEndpointTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `IKeyringHealth`
- Produces: `GET /api/system/keyring` → `{ status: "Healthy"|"Lost", accountsPending: int, backupConfigsPending: int }`；两个列表响应新增 `secretsUnavailable: bool`

- [ ] **Step 1: 写失败的测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/KeyringStatusEndpointTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;

namespace AzureStorageBackup.Api.Tests;

public class KeyringStatusEndpointTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record KeyringStatusResponse(string Status, int AccountsPending, int BackupConfigsPending);

    [Fact]
    public async Task Reports_Healthy_On_A_Fresh_Database()
    {
        var res = await _client.GetAsync("/api/system/keyring");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<KeyringStatusResponse>();
        Assert.Equal("Healthy", body!.Status);
        Assert.Equal(0, body.AccountsPending);
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~KeyringStatusEndpointTests`
Expected: FAIL，`404 NotFound`

- [ ] **Step 3: 实现端点**

`backend/src/AzureStorageBackup.Api/Endpoints/SystemEndpoints.cs` 在 `MapSystemEndpoints` 内追加：

```csharp
        // 密钥环状态与待重设计数（设计 §3.3），供顶部横幅与恢复清单使用。
        app.MapGet("/api/system/keyring", async (
            IKeyringHealth keyring, AppDbContext db, CancellationToken ct) =>
        {
            if (keyring.Status == KeyringStatus.Healthy)
                return Results.Ok(new
                {
                    status = nameof(KeyringStatus.Healthy),
                    accountsPending = 0,
                    backupConfigsPending = 0,
                });

            // Lost 时所有密文共用同一 protector，故全部解不开——直接计数，无需逐条试解。
            return Results.Ok(new
            {
                status = nameof(KeyringStatus.Lost),
                accountsPending = await db.Accounts.CountAsync(ct),
                backupConfigsPending = await db.BackupConfigs
                    .CountAsync(c => c.PasswordProtected != null && c.PasswordProtected != "", ct),
            });
        })
        .WithTags("System");
```

文件顶部补 `using AzureStorageBackup.Api.Data;`、`using AzureStorageBackup.Api.Services;`、`using Microsoft.EntityFrameworkCore;`。

- [ ] **Step 4: 列表响应加标记**

`backend/src/AzureStorageBackup.Api/Models/AccountDtos.cs`：`AccountResponse` 末尾增加记录字段 `bool SecretsUnavailable`，`From` 增加形参 `bool keyringLost = false` 并原样传入该字段（账户密钥必填，`Lost` 时必然解不开，无需额外条件）。`AccountEndpoints` 的列表与单条 handler 注入 `IKeyringHealth keyring`，调用时传 `keyring.Status == KeyringStatus.Lost`。

`backend/src/AzureStorageBackup.Api/Models/BackupConfigDtos.cs` 同样处理：`BackupConfigResponse` 末尾增加 `bool SecretsUnavailable`，`From` 增加形参 `bool keyringLost = false`，仅当该配置**有密码**且密钥环 `Lost` 时为 `true`：

```csharp
    public static BackupConfigResponse From(BackupConfig c, string activity = "Idle", bool keyringLost = false) => new(
        // ...既有参数不变...
        c.Status, c.LastError, c.LastErrorAt, activity,
        keyringLost && !string.IsNullOrEmpty(c.PasswordProtected));
```

两个 DTO 的形参统一叫 `keyringLost`（输入：密钥环状态），记录字段统一叫 `SecretsUnavailable`（输出：该条记录的表现）。`BackupConfigEndpoints` 中所有 `BackupConfigResponse.From(...)` 调用处补传第三个实参。

- [ ] **Step 5: 运行测试并提交**

Run: `dotnet test backend/AzureStorageBackup.slnx`
Expected: 全绿

```bash
git add -A backend/
git commit -m "feat: expose keyring status and pending-credential markers"
```

---

### Task 7: 账户凭据重设端点

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/AccountEndpoints.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/AccountResetSecretsTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `AccountKeyProtected`；`IBlobClientFactory.TestConnectionAsync`
- Produces: `POST /api/accounts/{id}/reset-secrets`，body `ResetAccountSecretsRequest(string AccountKey, string? ProxyPassword)`

- [ ] **Step 1: 写失败的测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/AccountResetSecretsTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;

namespace AzureStorageBackup.Api.Tests;

public class AccountResetSecretsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Rejects_Empty_Key()
    {
        var res = await _client.PostAsJsonAsync(
            "/api/accounts/1/reset-secrets", new { accountKey = "", proxyPassword = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Returns_404_For_Unknown_Account()
    {
        var res = await _client.PostAsJsonAsync(
            "/api/accounts/999999/reset-secrets", new { accountKey = "dGVzdA==", proxyPassword = (string?)null });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~AccountResetSecretsTests`
Expected: FAIL，`404 NotFound`（路由不存在）

- [ ] **Step 3: 实现端点**

`backend/src/AzureStorageBackup.Api/Endpoints/AccountEndpoints.cs` 在 group 内追加：

```csharp
        // 凭据重设（设计 §3.4）。不复用 PUT——PUT 在恢复模式下受限，且此处必须验证后才落库。
        group.MapPost("/{id:int}/reset-secrets", async (
            int id, ResetAccountSecretsRequest req, IAccountService svc, IBlobClientFactory factory,
            IEncryptionService encryption, AppDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.AccountKey))
                return Results.BadRequest(new { error = "AccountKey is required." });

            var existing = await svc.GetAsync(id, ct);
            if (existing is null)
                return Results.NotFound();

            // 用待验证的凭据构造一个临时账户对象去连云；验证不过则不落库。
            var candidate = new Account
            {
                Id = existing.Id,
                Name = existing.Name,
                BlobEndpoint = existing.BlobEndpoint,
                Region = existing.Region,
                UseProxy = existing.UseProxy,
                ProxyMode = existing.ProxyMode,
                ProxyHost = existing.ProxyHost,
                ProxyPort = existing.ProxyPort,
                ProxyUsername = existing.ProxyUsername,
                AccountKeyProtected = encryption.Encrypt(req.AccountKey),
                ProxyPasswordProtected = string.IsNullOrEmpty(req.ProxyPassword)
                    ? null : encryption.Encrypt(req.ProxyPassword),
            };

            var check = await factory.TestConnectionAsync(candidate, ct);
            if (!check.Success)
                return Results.BadRequest(new { error = $"Verification failed: {check.Error}" });

            var row = await db.Accounts.FirstAsync(a => a.Id == id, ct);
            row.AccountKeyProtected = candidate.AccountKeyProtected;
            row.ProxyPasswordProtected = candidate.ProxyPasswordProtected;
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });
```

在同文件的 DTO 区（或账户 DTO 文件）加：

```csharp
/// <summary>凭据重设请求。AccountKey 必填；ProxyPassword 为空表示清空代理密码。</summary>
public record ResetAccountSecretsRequest(string AccountKey, string? ProxyPassword);
```

文件顶部补 `using AzureStorageBackup.Api.Data;`、`using Microsoft.EntityFrameworkCore;`。

- [ ] **Step 4: 运行测试并提交**

Run: `dotnet test backend/AzureStorageBackup.slnx`
Expected: 全绿

```bash
git add -A backend/
git commit -m "feat: add verified account credential reset endpoint"
```

---

### Task 8: 备份密码重设端点与状态翻转

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/KeyringRecovery.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/AccountEndpoints.cs`（重设成功后调用恢复检查）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/KeyringRecoveryTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `KeyringProbe.WriteCanaryAsync` / `IKeyringHealth`；Task 1 的 `TryDecrypt`
- Produces: `KeyringRecovery.TryCompleteAsync(CancellationToken) -> Task<bool>`；`POST /api/backup-configs/{id}/reset-password`，body `ResetBackupPasswordRequest(string Password)`

- [ ] **Step 1: 写失败的测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/KeyringRecoveryTests.cs`：

```csharp
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

public class KeyringRecoveryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly IEncryptionService _current = new EncryptionService(new EphemeralDataProtectionProvider());
    private readonly IKeyringHealth _health = new KeyringHealth();

    public KeyringRecoveryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _health.Set(KeyringStatus.Lost);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private KeyringRecovery Sut() => new(_db, _current, _health, new KeyringProbe(_db, _current));

    private static string Stale(string v) =>
        new EncryptionService(new EphemeralDataProtectionProvider()).Encrypt(v);

    [Fact]
    public async Task Does_Not_Flip_While_A_Secret_Is_Still_Undecryptable()
    {
        _db.Accounts.Add(new Account { Name = "a", BlobEndpoint = "https://a.blob.core.windows.net", AccountKeyProtected = _current.Encrypt("k") });
        _db.Accounts.Add(new Account { Name = "b", BlobEndpoint = "https://b.blob.core.windows.net", AccountKeyProtected = Stale("k") });
        await _db.SaveChangesAsync();

        Assert.False(await Sut().TryCompleteAsync());
        Assert.Equal(KeyringStatus.Lost, _health.Status);
    }

    [Fact]
    public async Task Flips_To_Healthy_And_Rebuilds_Canary_When_All_Readable()
    {
        _db.Accounts.Add(new Account { Name = "a", BlobEndpoint = "https://a.blob.core.windows.net", AccountKeyProtected = _current.Encrypt("k") });
        _db.BackupConfigs.Add(new BackupConfig { Name = "docs", ContainerName = "c", LocalRoot = "/d", PasswordProtected = _current.Encrypt("pw") });
        await _db.SaveChangesAsync();

        Assert.True(await Sut().TryCompleteAsync());
        Assert.Equal(KeyringStatus.Healthy, _health.Status);
        Assert.Equal(1, await _db.KeyringCanaries.CountAsync());
    }

    [Fact]
    public async Task Unencrypted_Backup_Configs_Do_Not_Block_Recovery()
    {
        _db.BackupConfigs.Add(new BackupConfig { Name = "plain", ContainerName = "c", LocalRoot = "/d", PasswordProtected = null });
        await _db.SaveChangesAsync();

        Assert.True(await Sut().TryCompleteAsync());
        Assert.Equal(KeyringStatus.Healthy, _health.Status);
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~KeyringRecoveryTests`
Expected: 编译失败，`The type or namespace name 'KeyringRecovery' could not be found`

- [ ] **Step 3: 实现恢复完成判定**

创建 `backend/src/AzureStorageBackup.Api/Services/KeyringRecovery.cs`：

```csharp
using AzureStorageBackup.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 恢复完成判定（设计 §3.4）：所有密文都能用当前密钥环解开时，重建 canary 并翻回 Healthy。
/// 不可在首条重设成功时就翻转——彼时其余记录仍解不开。
/// </summary>
public sealed class KeyringRecovery(
    AppDbContext db, IEncryptionService encryption, IKeyringHealth health, KeyringProbe probe)
{
    public async Task<bool> TryCompleteAsync(CancellationToken ct = default)
    {
        var accountKeys = await db.Accounts.AsNoTracking()
            .Select(a => a.AccountKeyProtected).ToListAsync(ct);
        var proxyPasswords = await db.Accounts.AsNoTracking()
            .Where(a => a.ProxyPasswordProtected != null && a.ProxyPasswordProtected != "")
            .Select(a => a.ProxyPasswordProtected!).ToListAsync(ct);
        var backupPasswords = await db.BackupConfigs.AsNoTracking()
            .Where(c => c.PasswordProtected != null && c.PasswordProtected != "")
            .Select(c => c.PasswordProtected!).ToListAsync(ct);

        foreach (var cipher in accountKeys.Concat(proxyPasswords).Concat(backupPasswords))
        {
            if (string.IsNullOrEmpty(cipher))
                continue;
            if (!encryption.TryDecrypt(cipher, out _))
                return false;
        }

        await probe.WriteCanaryAsync(ct);
        health.Set(KeyringStatus.Healthy);
        return true;
    }
}
```

`Program.cs` 注册：

```csharp
builder.Services.AddScoped<KeyringRecovery>();
```

- [ ] **Step 4: 实现备份密码重设端点**

`backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs` 在 group 内追加：

```csharp
        // 备份密码重设（设计 §3.4）。验证依据：加密备份的信息文件本身就是用该密码加密的 7z，
        // 它是元数据根节点、容器内最小的加密对象，解得开即证明密码正确。
        group.MapPost("/{id:int}/reset-password", async (
            int id, ResetBackupPasswordRequest req, IBackupConfigService svc, IAccountService accounts,
            IBackupInfoStore store, IEncryptionService encryption, AppDbContext db,
            KeyringRecovery recovery, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(req.Password))
                return Results.BadRequest(new { error = "Password is required." });

            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            if (string.IsNullOrEmpty(config.PasswordProtected))
                return Results.BadRequest(new { error = "This backup is not encrypted; there is no password to restore." });

            var account = await accounts.GetAsync(config.AccountId, ct);
            if (account is null)
                return Results.BadRequest(new { error = "Account not found." });

            // 顺序依赖：连云需要账户密钥，故账户必须先恢复。
            try
            {
                // 纯读，不可用 TrackedInfoStore.SeedFromCloudAsync——那会回填本地权威状态。
                var info = await store.ReadInfoWithETagAsync(account, config.ContainerName, req.Password, ct);
                if (info is null)
                    return Results.BadRequest(new { error = "No backup info file found in the container." });
            }
            catch (SecretUnavailableException)
            {
                return Results.BadRequest(new { error = "Re-enter this backup's account credentials first." });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Verification failed: {ex.Message}" });
            }

            var row = await db.BackupConfigs.FirstAsync(c => c.Id == id, ct);
            row.PasswordProtected = encryption.Encrypt(req.Password);
            await db.SaveChangesAsync(ct);

            await recovery.TryCompleteAsync(ct);
            return Results.NoContent();
        });
```

在 DTO 区加：

```csharp
/// <summary>备份密码重设请求。必须是当初加密云端包的那个密码——不支持更改密码（设计决策 6、8）。</summary>
public record ResetBackupPasswordRequest(string Password);
```

- [ ] **Step 5: 账户重设成功后也尝试完成恢复**

Task 7 的 `reset-secrets` handler：参数加 `KeyringRecovery recovery`，`SaveChangesAsync` 之后、`return` 之前插入：

```csharp
            await recovery.TryCompleteAsync(ct);
```

- [ ] **Step 6: 全量测试并提交**

Run: `dotnet test backend/AzureStorageBackup.slnx`
Expected: 全绿

```bash
git add -A backend/
git commit -m "feat: add verified backup password reset and recovery completion"
```

---

### Task 9: 就绪探针本地化与死代码清理

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/HealthEndpoints.cs:15-22`
- Delete: `backend/src/AzureStorageBackup.Api/Services/AzureStorageService.cs`
- Delete: `backend/src/AzureStorageBackup.Api/Services/IAzureStorageService.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Program.cs:26-36`
- Modify: `backend/src/AzureStorageBackup.Api/appsettings.json:11`
- Modify: `docker-compose.yml:10-14`
- Modify: `.env.example`
- Modify: `README.md:62,78`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/HealthEndpointsTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `IKeyringHealth`

- [ ] **Step 1: 写失败的测试**

追加到 `backend/tests/AzureStorageBackup.Api.Tests/HealthEndpointsTests.cs`（该类使用 `WebApplicationFactory<Program>`，改用 `TestWebAppFactory` 以隔离数据库）：

```csharp
    [Fact]
    public async Task Ready_Returns_Ok_Without_Any_Cloud_Configuration()
    {
        // 就绪探针必须是纯本地的：无任何 Azure 连接串时依然 200（设计决策 10）
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~HealthEndpointsTests`
Expected: FAIL，503 —— 旧探针尝试连云失败

- [ ] **Step 3: 重写就绪探针**

`backend/src/AzureStorageBackup.Api/Endpoints/HealthEndpoints.cs` 的 `/api/health/ready` 整体替换：

```csharp
        // 就绪探针：仅检查本地依赖——SQLite 可连、密钥环可用。不访问云端（运行期零云读）。
        app.MapGet("/api/health/ready", async (
            AppDbContext db, IKeyringHealth keyring, CancellationToken ct) =>
        {
            var dbOk = await db.Database.CanConnectAsync(ct);
            var keyringOk = keyring.Status == KeyringStatus.Healthy;
            var body = new
            {
                status = dbOk && keyringOk ? "ready" : "degraded",
                database = dbOk,
                keyring = keyringOk,
            };
            return dbOk && keyringOk ? Results.Ok(body) : Results.Json(body, statusCode: 503);
        })
        .WithName("HealthReady")
        .WithTags("Health");
```

文件顶部补 `using AzureStorageBackup.Api.Data;`。

- [ ] **Step 4: 删除死代码链**

```bash
rm backend/src/AzureStorageBackup.Api/Services/AzureStorageService.cs \
   backend/src/AzureStorageBackup.Api/Services/IAzureStorageService.cs
```

`backend/src/AzureStorageBackup.Api/Program.cs` 删除以下三段（原 `:26-36` 区域）：连接串解析（`storageConn` 三段回退）、`builder.Services.AddSingleton(_ => new BlobServiceClient(storageConn));`、`builder.Services.AddScoped<IAzureStorageService, AzureStorageService>();`。若 `using Azure.Storage.Blobs;` 因此不再使用，一并删除。

`backend/src/AzureStorageBackup.Api/appsettings.json` 的 `ConnectionStrings` 段删除 `"AzureStorage": ""` 一行（保留 `Sqlite`，注意删掉前一行行尾逗号）。

- [ ] **Step 5: 清理部署配置与文档**

`docker-compose.yml` 删除 `ConnectionStrings__AzureStorage` 一行及其上方注释；保留 `Scheduler__TimeZone`。

`.env.example` 删除 `AZURE_STORAGE_CONNECTION_STRING` 及其注释。

`README.md`：从 `docker run` 示例（`:62`）中删除 `-e ConnectionStrings__AzureStorage=...` 一行；从环境变量表中删除 `ConnectionStrings__AzureStorage` 一行（`:78`）。在该表下方新增一段说明：

```markdown
> Azure credentials are **not** configured through environment variables — each storage account is added in the UI and its key is encrypted at rest with the Data Protection key ring in `/keys`. If that directory is lost, the app starts in recovery mode and asks you to re-enter each credential; see [keyring-loss-recovery-design.md](docs/keyring-loss-recovery-design.md).
```

- [ ] **Step 6: 全量测试并提交**

Run: `dotnet test backend/AzureStorageBackup.slnx`
Expected: 全绿

```bash
git add -A
git commit -m "refactor: make readiness probe local-only and drop the unused storage connection"
```

---

### Task 10: 前端恢复引导

**Files:**
- Create: `frontend/src/api/keyring.ts`
- Create: `frontend/src/components/KeyringBanner.tsx`
- Modify: `frontend/src/App.tsx`
- Modify: `frontend/src/api/accounts.ts`
- Modify: `frontend/src/api/backupConfigs.ts`
- Modify: `frontend/src/pages/AccountsPage.tsx`
- Modify: `frontend/src/pages/BackupConfigsPage.tsx`

**Interfaces:**
- Consumes: Task 6 的 `GET /api/system/keyring` 与两个列表的 `secretsUnavailable`；Task 7/8 的两个重设端点

- [ ] **Step 1: 新增 API 模块**

创建 `frontend/src/api/keyring.ts`：

```typescript
import { api } from './client'

export interface KeyringStatus {
  status: 'Healthy' | 'Lost'
  accountsPending: number
  backupConfigsPending: number
}

export const keyringApi = {
  status: () => api.get<KeyringStatus>('/system/keyring'),
}
```

在 `frontend/src/api/accounts.ts` 的 `Account` 接口加 `secretsUnavailable: boolean`，并在 `accountsApi` 中追加：

```typescript
  resetSecrets: (id: number, accountKey: string, proxyPassword: string | null) =>
    api.post<void>(`/accounts/${id}/reset-secrets`, { accountKey, proxyPassword }),
```

在 `frontend/src/api/backupConfigs.ts` 的备份配置接口加 `secretsUnavailable: boolean`，并追加：

```typescript
  resetPassword: (id: number, password: string) =>
    api.post<void>(`/backup-configs/${id}/reset-password`, { password }),
```

- [ ] **Step 2: 新增横幅组件**

创建 `frontend/src/components/KeyringBanner.tsx`：

```tsx
import { useEffect, useState } from 'react'
import { keyringApi, type KeyringStatus } from '../api/keyring'

/**
 * 密钥环丢失时的常驻横幅（设计 §3.5）。文案一律英文。
 * 账户必须先恢复——验证备份密码需要连云，连云需要账户密钥。
 */
export function KeyringBanner({ onGoToAccounts }: { onGoToAccounts: () => void }) {
  const [status, setStatus] = useState<KeyringStatus | null>(null)

  useEffect(() => {
    keyringApi.status().then(setStatus).catch(() => setStatus(null))
  }, [])

  if (!status || status.status === 'Healthy') return null

  const pending = status.accountsPending + status.backupConfigsPending

  return (
    <div
      role="alert"
      style={{
        border: '1px solid #b45309',
        background: '#fffbeb',
        color: '#7c2d12',
        padding: '0.75rem 1rem',
        borderRadius: 6,
        marginBottom: '1rem',
      }}
    >
      <strong>Data protection keys were lost</strong> — {pending} credential
      {pending === 1 ? '' : 's'} need to be re-entered before backups can run.
      {status.accountsPending > 0 && (
        <>
          {' '}
          Start with{' '}
          <button type="button" onClick={onGoToAccounts}>
            Accounts
          </button>
          {' '}({status.accountsPending} pending), then re-enter backup passwords
          ({status.backupConfigsPending} pending).
        </>
      )}
    </div>
  )
}
```

- [ ] **Step 3: 挂到 App 外壳**

`frontend/src/App.tsx`——引入组件，并在 `<nav>` 之后、页面渲染之前插入：

```tsx
import { KeyringBanner } from './components/KeyringBanner'
```

```tsx
      <KeyringBanner onGoToAccounts={() => setTab('accounts')} />
```

- [ ] **Step 4: 账户页内标记与重设**

`frontend/src/pages/AccountsPage.tsx`——在账户列表每一行渲染处，当 `a.secretsUnavailable` 为真时，在名称旁加标记与按钮：

```tsx
{a.secretsUnavailable && (
  <>
    <span style={{ color: '#b45309', marginLeft: '0.5rem' }}>Credential required</span>
    <button type="button" onClick={() => setResetting(a)}>Re-enter</button>
  </>
)}
```

新增状态 `const [resetting, setResetting] = useState<Account | null>(null)`，并在页面底部渲染重设表单（复用 `components/modal.tsx` 的既有弹窗样式）。提交逻辑：

```tsx
const submitReset = async (accountKey: string, proxyPassword: string) => {
  if (!resetting) return
  setBusy(true)
  try {
    await accountsApi.resetSecrets(resetting.id, accountKey, proxyPassword || null)
    setResetting(null)
    load()
  } catch (e) {
    setError(String(e))
  } finally {
    setBusy(false)
  }
}
```

后端在验证失败时返回 400 与 `Verification failed: ...`，`ApiError.message` 会带上该文本，直接显示即可。

- [ ] **Step 5: 备份配置页内标记与重设**

`frontend/src/pages/BackupConfigsPage.tsx`——同 Step 4 的模式，标记文案用 `Password required`，调用 `backupConfigsApi.resetPassword(id, password)`。表单只含一个密码输入框，提示文案：

```
Enter the original password used to encrypt this backup. It cannot be changed — a different password will fail verification.
```

同时：当账户仍有待重设项时（`keyringApi.status()` 的 `accountsPending > 0`），禁用该按钮并加 title：`Re-enter account credentials first`。

- [ ] **Step 6: 构建校验**

```bash
cd frontend && npm run build
```

Expected: 构建成功，无 TypeScript 报错

- [ ] **Step 7: 提交**

```bash
git add -A frontend/
git commit -m "feat: guide credential re-entry from the UI when the keyring is lost"
```

---

## 完成后的验证

- [ ] `dotnet test backend/AzureStorageBackup.slnx` 全绿
- [ ] `cd frontend && npm run build` 成功
- [ ] 手工验证恢复流程：启动应用 → 添加一个账户 → 停止 → 删除 `/keys`（或本地 `keys/`）目录 → 重启 → 确认账户列表**仍能打开**、顶部出现横幅、`/api/health/ready` 返回 503、备份动作返回 409 → 重新录入账户密钥 → 确认横幅消失、`/api/health/ready` 恢复 200
