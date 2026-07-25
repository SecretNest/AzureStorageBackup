# 密钥环丢失的检测与恢复（2026-07-25）

> Data Protection 密钥环（`/keys`）丢失后，库中密文字段全部无法解密。当前实现会在 EF 实体物化时抛异常，导致账号列表、备份配置列表整体 500，用户连界面都进不去，更无从修复。本文件固化该问题的设计决策与方案：**密文入库、按需解密**，配合 canary 检测与引导式重新录入。
>
> 顺带清理 `ConnectionStrings__AzureStorage` 及其死代码链。补充 [product-requirements.md](product-requirements.md)、[backup-feature-design.md](backup-feature-design.md)。

## 1. 设计决策（本轮锁定）

| # | 决策点 | 结论 |
|---|--------|------|
| 1 | 密文的解密时机 | **去掉 ValueConverter，密文原样入库**。解密只发生在真正读懂内容的咽喉处，列表/主界面不触发解密，密钥环丢失时 UI 照常可用。 |
| 2 | 属性命名 | 敏感属性加 `Protected` 后缀（`AccountKeyProtected` 等），用 `HasColumnName` 保持数据库列名不变。改名的编译错误清单即为需逐个审计的使用点清单。 |
| 3 | 密钥环健康检测 | 单行表 `KeyringCanary` 存已知常量明文的密文，**不走任何转换**，由服务层显式 `Protect`/`Unprotect`。启动判定一次，结果缓存在单例。 |
| 4 | 升级老库的判定 | canary 行不存在时，**确定性地取 `Id` 最小的 Account** 的密钥密文试解（无 Account 则回退到 `Id` 最小且密码非空的 BackupConfig）。不可用「任取一条」——EF 无 `OrderBy` 的 `FirstOrDefault` 返回顺序不确定，判定不可复现。 |
| 5 | 重设密码的验证 | **验证通过才落库**。账号走已有的 `TestConnectionAsync`；备份密码拉云端加密信息文件试解——它是备份的元数据根节点，全容器最小的加密对象，不碰数据包、不触发 Archive 取回费。 |
| 6 | 想不起备份密码的出口 | **无出口**。不提供「放弃历史、改用新密码」，也不做历史包重加密迁移。密码想不起来只能删除该备份配置重建。 |
| 7 | 恢复顺序 | **先账号、后备份配置**。验证备份密码必须连云，连云必须先有账号密钥——物理约束，UI 强制该顺序。 |
| 8 | 备份密码不可更改 | 普通 PUT 路径下 `Password` 非空一律拒绝，重设走专用端点。 |
| 9 | `ConnectionStrings__AzureStorage` | **删除**，连同全局 `BlobServiceClient` 单例与 `IAzureStorageService`/`AzureStorageService`。 |
| 10 | `/api/health/ready` | 保留，改为**纯本地**就绪检查（SQLite 可打开 + canary 可解），零云读。 |

## 2. 现状与根因

敏感字段共三个，均通过 `AppDbContext.cs:28-33` 定义的 ValueConverter 在落库边界自动加解密：

| 字段 | 位置 | 可空 |
|---|---|---|
| `Account.AccountKey` | `AppDbContext.cs:40` | 否（`IsRequired`） |
| `Account.ProxyPassword` | `AppDbContext.cs:41` | 是 |
| `BackupConfig.Password` | `AppDbContext.cs:70` | 是 |

**根因**：ValueConverter 在 EF **实体物化**时无条件解密，与调用方是否需要该字段无关。`db.Accounts.ToListAsync()` 即使只为读 `Name`，也会对每一行的密钥密文调用 `Decrypt`。密钥环丢失后该调用抛 `CryptographicException`，且代码中无任何捕获或降级路径，于是列表查询整体失败。

**关键观察**：这三个字段真正被「读懂」的地方极少，其余全是搬运——而搬运密文与搬运明文等价。

| 消费点 | 位置 | 用途 |
|---|---|---|
| `AccountKey` | `BlobClientFactory.cs:28` | 构造 `StorageSharedKeyCredential` |
| `ProxyPassword` | `BlobClientFactory.cs:74` | 构造 `NetworkCredential` |
| `Password` | `SevenZipCompressor.cs:37,39`、`SevenZipArchiveCodec.cs:35,62` | 传给 7z 的 `-p` |

搬运点（`AccountService.cs:35,41`、`AccountEndpoints.cs:43-46`、`BackupRequestMapper.cs:88`、`RestoreRunner.cs:83`、`TaskDispatcher.cs:78`、`BackupOrchestrator`/`RestoreOrchestrator` 各处、`BackupConfigEndpoints.cs` 各处）不解读内容，不需要明文。

## 3. 方案

### 3.1 密文入库，咽喉处解密

移除 `AppDbContext.cs:28-33` 的两个 ValueConverter，三个字段在 EF 层原样存取密文；加解密由服务层显式进行。属性按决策 2 改名加 `Protected` 后缀，并以 `HasColumnName` 固定原列名。

**解密落点（咽喉，而非散落检查）：**

- **账号密钥与代理密码** —— `BlobClientFactory` 内部。`CreateServiceClient` 是所有云操作的唯一入口，任何访问 Azure 的路径都必须经过它，在 `:28`、`:74` 就地解密即可，无需在各动作入口重复。
- **备份密码** —— 两个集中点：`BackupRequestMapper.Password(config)`（`BackupRequestMapper.cs:88`，被 `TaskDispatcher.cs:78` 等调用）与 `BackupConfigEndpoints.cs` 中重复出现六次的 `var password = string.IsNullOrEmpty(config.Password) ? null : config.Password;`（`:190,215,239,265,361,389`）。后者收敛为一个统一的解密辅助方法，既是解密入口也顺带消除重复。

解密后链路中流动的仍是明文，所有传递代码无需改动。解密失败抛 `SecretUnavailableException`，错误精确定位到发起的那个动作。

`SecretUnavailableException` 与 3.3 的 409 闸门是两个层次：canary 判定 `Lost` 时，动作端点在入口即以 409 快速失败，正常情况下走不到解密处；该异常是深度防御，用于「闸门被新代码路径绕过」或「密钥环在进程运行期间被替换」等 canary 尚未覆盖的情形，确保失败方式明确，而非产出用错误密码加密的包。

**因此获得的性质：**主界面、账号列表、备份配置列表只查非敏感字段，根本不触发解密，密钥环丢失时完全可用；只有具体动作（列容器、备份、还原、检查）才需要凭据。

**自动消除的问题：**`AccountEndpoints.cs:43-46` 与 `BackupConfigEndpoints.cs:98-99` 的「提交空值 = 保留原值」逻辑，在密文模型下就是把 existing 的密文原样搬运，正确且无需解密。

**需配套调整的两处：**

1. `BackupConfigDtos.cs:39` 与 `BackupOrchestrator.cs:729` 用 `!string.IsNullOrEmpty(Password)` 判断「是否加密备份」。密文非空 ⟺ 明文非空，判定依然成立，**无需改动**。
2. `BackupConfigService.cs:49` 的 `update.Password != existing.Password` **必须改**。Data Protection 每次加密使用随机 IV，同一明文两次加密得到不同密文，密文之间不可比较。该行的意图正是「密码创建后不可更改」，按决策 8 改为：普通 PUT 路径下 `Password` 非空一律拒绝，提示 `Password cannot be changed after creation; leave it empty.`。行为上的唯一变化是「重新提交完全相同的密码」由放行变为拒绝——前端本就以留空表示不改。

### 3.2 Canary 与状态判定

新增单行表 `KeyringCanary`（`Id`、`Ciphertext`、`CreatedAt`）。`Ciphertext` 为常量明文 `canary.v1` 的密文，**不经任何转换**存取，由服务层显式 `Protect`/`Unprotect`——否则 canary 自身也会被降级逻辑吞掉而失去判定意义。

`IKeyringHealth`（单例）持有 `KeyringStatus = Healthy | Lost`，进程启动时判定一次并缓存，重设流程完成时显式翻转。

**启动判定：**

| canary 行 | 探测源 | 结论 |
|---|---|---|
| 存在 | canary 自身 | 解得开 `Healthy`；解不开 `Lost` |
| 不存在 | `Id` 最小的 Account 的密钥密文 | 解得开 → 写入 canary，`Healthy`；解不开 → `Lost` |
| 不存在，且无 Account | `Id` 最小且密码非空的 BackupConfig | 同上 |
| 不存在，且两者皆无 | — | 全新库 → 写入 canary，`Healthy` |

第二行是升级老库的必经分支：老版本库没有 canary 行，若无脑写入一条新的即判 `Healthy`，则「升级时密钥环恰好已丢失」会被漏检，且从此永远检测不出——新 canary 由新密钥环写入，永远解得开。第三行是廉价兜底，仅在一条账号都没有时多查一次，堵住「账号被删光但备份配置仍在」的窄窗口。

### 3.3 恢复模式（`Lost`）的行为边界

- `SchedulerService` 的 tick 跳过全部任务，每个 tick 只记**一条**汇总 Warning（非每任务一条，否则日志被刷爆）
- 手动触发备份/还原/检查/清理的端点返回 **409**，错误码 `keyring_lost`
- 账号列表、备份配置列表**照常返回**，附带 `secretsUnavailable: true`
- `/api/health/ready` 返回 503 `degraded`
- 唯一放行的写操作：重设凭据

待重设计数与逐条 `secretsUnavailable` 标记**必须按每条记录的实际可解性判定**，不能沿用全局状态。

「`Lost` 即全部解不开」只在密钥环刚丢失的那一刻成立。恢复必然经过一个中间态：账户已全部重设成功，备份密码仍是旧密文——此时全局状态仍须是 `Lost`（3.4 的完成判定要求三族密文全部可解），但账户的待重设数必须已经归零。若按全局状态直接计数，3.5 的顺序依赖（账户未清零则禁用备份密码的 `Re-enter`）会永远读到非零的账户待重设数，按钮永不可用，密码永不能重设，状态永不翻转——恢复流程在 UI 上彻底死锁。

因此 `Lost` 期间读取密文列逐条试解：账户看密钥与代理密码（`reset-secrets` 一次重设两者），备份配置看密码（未加密的没有密文可丢，不计不标）。`Healthy` 时短路返回 0，列表端点仍然完全不触发解密（3.1 的核心性质不受影响）。记录数很少，与 3.4 的完成扫描同量级，开销可忽略。

### 3.4 重设流程

两个专用端点，不复用 PUT（PUT 在恢复模式下应整体受限）：

- `POST /api/accounts/{id}/reset-secrets` —— body 含 `accountKey`、可选 `proxyPassword`；先走 `BlobClientFactory.TestConnectionAsync`（`BlobClientFactory.cs:38`）验证，通过才落库
- `POST /api/backup-configs/{id}/reset-password` —— body 含 `password`；拉云端加密信息文件试解，通过才落库

**验证备份密码的依据**：加密备份的信息文件（`BackupDiscovery.EncryptedIndexBlobName`）本身就是用备份密码加密的 7z（`BackupInfoStore.cs:38-43` → `WriteAtomicAsync` → `codec.EncodeAsync(json, password)`）。它是整个备份的元数据根节点，仅含版本列表等少量信息，是容器内最小的加密对象。解得开即证明密码正确。

**实现约束**：验证必须调用 `IBackupInfoStore.ReadInfoWithETagAsync`（纯读，`BackupInfoStore.cs:14-29`），**不可**使用 `TrackedInfoStore.SeedFromCloudAsync`（`TrackedInfoStore.cs:53-60`）——后者会回填本地权威状态。验证是可能失败、可能被反复尝试的操作，不允许有副作用。

未加密的备份配置（`Password` 为 null）没有密钥可丢，不进入待重设清单。

**完成判定**：对所有含密文的记录逐条试解，全部成功后重建 canary 并将状态翻回 `Healthy`。不可在首条重设成功时就翻转——彼时其余记录仍解不开。该扫描在恢复流程末尾执行一次，记录数很少，开销可忽略。

### 3.5 UI 引导

界面文案一律英文。

- 恢复模式下顶部常驻横幅：`Data protection keys were lost — N credentials need to be re-entered`，点击展开待重设清单
- 清单按 **Accounts → Backup Configs** 两组排列；账号组未全部完成前，备份配置组保持禁用，以体现决策 7 的顺序依赖
- 各自页面内，受影响行显示 badge 与 `Re-enter` 按钮，打开仅含密码字段的重设弹窗，带「验证中 / 验证失败」反馈
- 备份、还原、检查等动作按钮在恢复模式下禁用，并以 tooltip 说明原因

### 3.6 死代码清理

`ConnectionStrings__AzureStorage` → 全局 `BlobServiceClient` 单例（`Program.cs:33`）→ `IAzureStorageService`/`AzureStorageService` → 仅被 `/api/health/ready` 调用。前端从未调用该端点，测试亦未引用 `AzureStorageService`。真正的备份走 `BlobClientFactory.CreateServiceClient(account)`，与该链无关。该探针每次向云端发起 `GetProperties`，与「运行期零云读」原则相冲突。

删除范围：`Program.cs:26-36`（连接串解析、单例注册、服务注册）、`Services/AzureStorageService.cs`、`Services/IAzureStorageService.cs`、`appsettings.json:11`、`docker-compose.yml:11-12`、`.env.example` 中的 `AZURE_STORAGE_CONNECTION_STRING`、`README.md:62,78`。

`/api/health/ready`（`HealthEndpoints.cs:15-22`）改为检查 SQLite 可打开 + canary 可解密，二者皆本地。

## 4. 数据与迁移

**现有数据零迁移**。ValueConverter 当前写入磁盘的正是 `_protector.Protect(plaintext)` 的结果；改为「原样存密文」后磁盘格式完全一致。属性改名通过 `HasColumnName` 保持列名不变，同样不产生列变更。

唯一的 migration 是新建 `KeyringCanary` 表。项目在 `Program.cs:156` 启动时执行 `db.Database.Migrate()`，新表随启动自动创建。

## 5. 测试计划

- **canary 判定四分支**：全新库、老库密文解得开、老库密文解不开、canary 行存在但解不开
- **密文往返**：写入后库中为密文、读出仍为密文、咽喉处解密还原出原明文
- **关键回归**：密钥环丢失时账号列表与备份配置列表查询**依然成功**（这是本轮改动的核心目的）
- **恢复模式闸门**：`SchedulerService` 在 `Lost` 下跳过任务且每 tick 仅记一条汇总日志；动作端点返回 409 `keyring_lost`
- **重设**：验证失败不落库；验证成功才落库；验证路径无副作用（不回填本地权威状态）
- **状态翻转**：全部记录重设完成后 canary 重建、状态回到 `Healthy`；仅完成部分时不翻转
- **`BackupConfigService`**：普通 PUT 提交非空密码被拒
- **`/api/health/ready`**：纯本地判定，`Lost` 时返回 503
- **既有用例回归**：现有 332 个测试中受 ValueConverter 移除与属性改名影响的部分

## 6. 明确不做

- 不提供备份密码的更改或迁移功能（历史包重加密）
- 不提供「放弃历史、改用新密码」的出口（决策 6）
- 不引入 `ProtectedValue` 值类型——决策 2 的改名已使误用需要显式动作，额外收益不足以抵消其侵入性
- 不做密文字段的逐条降级**落库**标记——`secretsUnavailable` 与待重设计数是读时按实际可解性算出来的（3.3），不新增任何持久化列
