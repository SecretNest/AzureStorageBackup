using System.Text.Json.Nodes;

namespace AzureStorageBackup.Api.Models;

// 信息记录文件（权威元数据 blob，PRD 1.5）与第二级版本索引的数据模型（M4 设计 §3）。

/// <summary>信息记录文件（§3.1）：配置 + 版本列表 + 分组包元数据。跨设备恢复的唯一真相源。</summary>
public sealed record BackupInfoFile
{
    public int SchemaVersion { get; init; } = 1;
    public required BackupMeta Backup { get; init; }
    public List<BackupVersion> Versions { get; init; } = [];
    public Dictionary<string, PackInfo> Packs { get; init; } = [];
}

/// <summary>备份配置快照（创建后不可改，除名字/描述）。</summary>
public sealed record BackupMeta
{
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>源根路径提示，仅供参考；恢复时用户重新指定（§3.1）。</summary>
    public string? SourceRootHint { get; init; }

    public bool Encrypted { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>本备份生效的设置（默认值解析结果快照）。schema 待 M4 设置页定稿，暂用开放对象。</summary>
    public JsonObject? Settings { get; init; }

    /// <summary>
    /// 加密备份的密钥派生盐（首次创建时随机生成）。用于 data blob 的密钥化寻址（防指纹识别）：
    /// key = HKDF(password, KdfSalt)，blob 名 = data/{HMAC(key, fullHash)}。非加密备份为 null。
    /// </summary>
    public byte[]? KdfSalt { get; init; }
}

/// <summary>一个不可变版本（§3.1 versions[]），引用其第二级索引。</summary>
public sealed record BackupVersion
{
    public int Version { get; init; }

    /// <summary>版本提交时刻（备份结束）。收尾清理在此之后还要跑一阵，不计入。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>本次备份开始跑的时刻。info format 3 之前写下的版本没有这个信息 → null，
    /// 无法回填（猜出来的数字比空着更坏）。</summary>
    public DateTimeOffset? StartedAt { get; init; }

    public required string IndexBlob { get; init; }
    public required VersionStats Stats { get; init; }
}

/// <summary>版本统计（进度/展示用；删除不计入 changed）。</summary>
public sealed record VersionStats(long Files, long Bytes, long ChangedFiles, long ChangedBytes);

/// <summary>分组包元数据（§6 死重压实跟踪）。</summary>
public sealed record PackInfo
{
    public required string Blob { get; init; }
    public List<string> Members { get; init; } = [];
    public long OriginalBytes { get; init; }
    public long DeadBytes { get; init; }

    /// <summary>pack 归档的分卷数（1=未分卷）。压实会改变，随 PackInfo 一并更新，供检查核验全部分卷存在（§7）。</summary>
    public int Volumes { get; init; } = 1;

    /// <summary>各分卷字节尺寸（按 .001..N 顺序）。供「存在+尺寸」级检查免下载发现截断/错包。旧信息文件可能为空（→仅验存在）。</summary>
    public List<long> VolumeSizes { get; init; } = [];
}

/// <summary>第二级版本索引（§3.2）：该版本全部文件清单 + 空文件夹。</summary>
public sealed record VersionIndex
{
    public int Version { get; init; }
    public List<IndexEntry> Entries { get; init; } = [];

    /// <summary>空文件夹（备份需包含，还原需创建）。</summary>
    public List<string> EmptyDirs { get; init; } = [];

    /// <summary>此版本中已无法恢复的文件路径（云端损坏且本地也无法修复）。由修复流程写入；
    /// 还原时据此让用户逐个从其它版本替代。</summary>
    public List<string> UnrecoverablePaths { get; init; } = [];
}

/// <summary>索引条目：一个文件/符号链接及其存储位置。</summary>
public sealed record IndexEntry
{
    public required string Path { get; init; }

    /// <summary>"file" | "symlink"。</summary>
    public required string Kind { get; init; }

    public long Length { get; init; }
    public DateTimeOffset Mtime { get; init; }
    public required string Permissions { get; init; }

    public string? HeadHash { get; init; }

    /// <summary>文件末段 hash（§ 去重碰撞加固）。与 HeadHash/Length/FullHash 一起构成内容身份，
    /// 使自建备份可纯本地（不读云端）判断去重/碰撞。旧索引可能为 null。</summary>
    public string? TailHash { get; init; }

    public string? FullHash { get; init; }

    /// <summary>symlink 目标（仅 kind=symlink）。</summary>
    public string? Target { get; init; }

    /// <summary>本轮未能重读该文件（被占用/无权限/读错误），条目内容沿用上一版本。
    /// null = 本版本正常读取。值为发生时刻，便于操作员判断这份旧内容有多旧。</summary>
    public DateTimeOffset? UnreadableAt { get; init; }

    public StorageRef? Storage { get; init; }
}

/// <summary>条目存储位置：单文件 blob 或分组 pack 内成员。</summary>
public sealed record StorageRef
{
    /// <summary>"blob" | "pack"。</summary>
    public required string Kind { get; init; }

    /// <summary>blob: data/{fullHash}；pack: packId。</summary>
    public required string Ref { get; init; }

    /// <summary>pack 内条目名（仅 kind=pack）。</summary>
    public string? EntryName { get; init; }

    /// <summary>
    /// 单文件 blob 的分卷数（1=未分卷，§7）。内容寻址不可变，故计数稳定，供检查核验全部分卷存在。
    /// pack 成员此值无意义（pack 分卷数记在 <see cref="PackInfo.Volumes"/>，因压实会改变）。
    /// </summary>
    public int Volumes { get; init; } = 1;

    /// <summary>
    /// 该 blob 是**原始文件字节**而非 7z 归档（PRD 3.3.2：未压缩+未加密+无需分卷时直传原文件，省一次封装）。
    /// 仅单文件 blob；还原/检查据此直接复制/哈希，不解压。
    /// </summary>
    public bool Raw { get; init; }

    /// <summary>各分卷字节尺寸（按 .001..N 顺序）。供「存在+尺寸」级检查免下载发现截断/错包。旧索引可能为空（→仅验存在）。</summary>
    public List<long> VolumeSizes { get; init; } = [];
}
