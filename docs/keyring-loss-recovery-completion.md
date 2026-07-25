# 密钥环丢失恢复 —— 完成记录（2026-07-25）

> 依 [keyring-loss-recovery-design.md](keyring-loss-recovery-design.md) 与 [keyring-loss-recovery-plan.md](keyring-loss-recovery-plan.md) 实施，10 个计划任务全部完成，另按需求追加一套端到端生命周期测试。逐任务评审 + 整分支终审 + 遗留项清理共四轮，全部通过。本文件固化交付内容、期间发现的产品缺陷、以及已知的设计权衡。
>
> 合并至 `main`：`f67a2a0`（主体）、`a917aa3`（遗留项清理）。最终 **416 项测试全绿、0 skipped**（CI 内 Azurite 在跑，集成测试真实执行），`dotnet build` 0 warnings，前端 build + oxlint 干净。

## 1. 交付内容

| # | 内容 | 要点 |
|---|------|------|
| 1 | 密文入库 | 移除 EF `ValueConverter`，三个敏感字段原样存取密文；解密只发生在 `ISecretReader` 与 7z 边界。列表查询不再触发解密，密钥环丢失时 UI 照常可进 |
| 2 | 属性改名 | `AccountKeyProtected` / `ProxyPasswordProtected` / `PasswordProtected`，以 `HasColumnName` 固定原列名。**零数据迁移** |
| 3 | Canary 检测 | 单行表 `KeyringCanary` + 六分支启动判定（含升级老库、陈旧 canary 的逃生口） |
| 4 | 恢复模式闸门 | 9 个消费凭据的端点返回 409 `keyring_lost`；调度器跳过任务但**继续**清理日志；`SecretUnavailableException` 经中间件映射为 409 |
| 5 | 验证式重设 | 账号走 `TestConnectionAsync`；备份密码解云端加密信息文件（元数据根节点，容器内最小的加密对象）。验证不通过绝不落库 |
| 6 | 状态翻转 | 三族密文全部可解才回 `Healthy`；待重设计数按**逐条实际可解性**判定 |
| 7 | UI 引导 | 顶部横幅 + 逐条 badge + 重设弹窗；账号未清零前禁用备份密码重设（顺序依赖） |
| 8 | 就绪探针本地化 | `/api/health/ready` 只查 SQLite + canary，零云读 |
| 9 | 死代码清理 | 删除 `ConnectionStrings__AzureStorage` → 全局 `BlobServiceClient` → `IAzureStorageService` 整条链 |
| 10 | 端到端测试 | `BackupLifecycleTests` + `BackupImportLifecycleTests`：备份 → 增量 → 压实 → 检查（含损坏与修复）→ 还原 → 导入空环境，加密与不加密各走一遍 |

## 2. 发现并修复的产品缺陷（本轮之前即存在）

端到端测试补上了「把各阶段串成一条真实数据流」的空白 —— 此前 300+ 项测试逐组件覆盖，组件各自正确不等于串起来正确。它立刻照出两个既有缺陷：

### 2.1 修复加密备份会把数据写成明文（机密性，高危）

`BackupRepairer.ReplaceBlobAsync` 把 `CompressionRequest` 的密码硬编码为 `null`（兄弟方法 `RepairPackAsync` 传对了）。于是对**加密**备份执行修复，被重造的单文件 data blob 以**明文 7z** 落回云端。

**危害在于零症状**：7z 对未加密归档忽略 `-p`，所以检查与还原照常通过，用户不会察觉，除非直接查看云端 blob。加密备份的全部意义就是防住存储侧读取，修复一次即破功。

修复：把已在作用域内的 `password` 贯通下去。守护用**存储层探测**（下载 blob，要求无密码解压失败、有密码解压成功），不能靠「还原得出来」判定 —— 那正是当初让缺陷隐形的原因。

### 2.2 修复会抹掉碰撞检测元数据（完整性，中危）

同一方法未向 `VolumeBlobIO.ReplaceAsync` 传 `metadata`，被修复的 blob 丢失 `len`/`head`/`tail`，三段 hash 防碰撞对这些对象失效。

修复：复用索引自身已记录的值重建，保证与新鲜备份**逐字节一致** —— 写入「存在但不同」的元数据会让同内容被判成碰撞、改走 `~N` 备用地址并误报，比原缺陷更糟。

## 3. 评审拦下的缺陷

逐任务评审与整分支终审共拦下 1 项 Critical、7 项 Important。其中两类值得记录，因为它们是单个任务的 diff 看不见的：

### 3.1 恢复流程死锁（Critical，整分支终审发现）

三块各自正确的代码组合成一个环：

1. 待重设计数由**全局状态**派生（设计 §3.3 原文：「Lost 即意味着全部解不开，直接计数」），不随重设递减
2. 翻回 `Healthy` 要求三族密文**全部**可解，含备份密码
3. 前端用 `accountsPending > 0` 禁用备份密码的重设按钮

结果：用户把账号全部重设成功后，状态仍是 `Lost`（备份密码还是旧密文），计数仍报 N，按钮永久禁用，密码永远无法重设，状态永远不翻转 —— **恢复在 UI 上彻底走不通**。

根因是设计的一句论断只在**丢失那一刻**成立，在**恢复中间态**不成立；而测试只覆盖了「全丢」与「全好」两端，恰好跨过了真实恢复必经的唯一中间状态。已改为逐条试解，并补了中间态测试。设计 §3.3 同步更正。

### 3.2 验证被完全绕过（Important）

`ReadInfoWithETagAsync` 优先读**未加密**的信息文件（以 `password: null`），找不到才回退加密名。而重设端点只凭**本地** `PasswordProtected` 非空判断「这是加密备份」，从不检查返回内容。于是对「本地记为加密、容器内却存在明文信息文件」的配置，**任意**密码都能「验证通过」并落库，真密码永久丢失 —— 「密码不可更改」的约束出现静默逃生口。已加 `info.Backup.Encrypted` 守卫。

### 3.3 其余

- `/api/tasks/{id}/run`（计划任务的手动触发）无闸门：`Lost` 时内部记错误日志却返回 **200 OK** 并推进 `LastRunAt`，UI 显示成功
- 容器端点无闸门且异常无 HTTP 映射 → 裸 500
- 陈旧 canary 可令 `Lost` 成为**终态**：用户放弃恢复、删光记录后库中一条密文不剩，却被永久钉在恢复模式，横幅还写着「0 credentials need to be re-entered」
- `PUT /api/accounts/{id}` 在保留原密文的分支上回报 `secretsUnavailable: false`，与状态端点的计数自相矛盾

## 4. 遗留项清理

主体合并后另开一轮，清空全部 Minor，不留尾巴：

- 删除 `IEncryptionService.Decrypt` —— 移除 ValueConverter 后生产已无调用者，但它抛裸 `CryptographicException`，是「解密失败必须是 `SecretUnavailableException`」的一条活旁路
- 两处 `FirstAsync` → `FirstOrDefaultAsync`（删除竞态返回 404 而非 500）
- 四处 catch 不再吞 `OperationCanceledException`（原以为一处；其中 `/check` 会把客户端断开持久化成配置的 Error 状态）
- 修复路径改为优先取**持有完整 hash** 的版本引用（原按字典枚举顺序取 `refs[0]`，可能丢弃兄弟版本已有的元数据），无法保留时写审计日志
- 修复归档的 `StoreOnly` 改为由 `DontCompress` 规则派生，与新鲜备份一致
- **keyed 碰撞守卫补窄验证器 `v1`**：keyed 模式的 `v` 覆盖四项输入，任一项未知只能整体省略，导致修复过的 legacy 对象**没有任何验证元数据**，守卫塌缩为仅剩非加密 `fullHash`。新增只覆盖 `fullHash|len` 的 `v1`，把降级从「只剩 hash」拉回「hash + 长度」，且不泄露 keyed 模式刻意隐藏的长度。向后兼容：带 `v` 的旧对象判定完全不变，两者皆无的仍按「无元数据、不参与判定」处理

## 5. 已知的设计权衡

以下是**有意为之**的选择，不是欠账：

- **想不起备份密码没有出口**（决策 6）。不提供「放弃历史、改用新密码」，也不做历史包重加密。云端包由用户自己设的密码加密，系统只保存副本；副本解不开时必须由用户重新录入**同一个**密码，验证不过就拒绝。想不起来只能删除该备份配置重建。
- **恢复必须先账号、后备份密码**（决策 7）。验证备份密码要连云，连云要账号密钥 —— 物理约束，前后端各自独立强制。
- **碰撞守卫是第二道防线**。内容寻址用非加密的 XxHash128，`len`/`head`/`tail` 用于拦截 `fullHash`+长度碰撞。`MetadataMatches` 只在**云端回退导入路径**生效；正常去重走本地权威索引，以 `fullHash\nlen\nhead\ntail` 精确比对，缺失 hash 的老条目在那里根本匹配不上，会被既有机制导向 `~N`，失败方向安全。
- **`RepairRunner` 的一行接线未被测试覆盖**。仓库中没有 `RepairRunner` 测试基架，为一个表达式搭建完整 DI 主机 + Azurite + 后台轮询不成比例。缓解手段是把新参数设为**必需**，让编译器强制每个调用点显式表态。

## 6. 结论

无已知未决项。设计文档 §3.2、§3.3、§3.1 已随实现同步更正 —— 其中 §3.3 的更正来自上面 3.1 那条 Critical，原文的论断本身就是缺陷来源。
