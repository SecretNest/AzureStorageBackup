namespace AzureStorageBackup.Api.Models;

/// <summary>推送方法（PRD 4.2）。</summary>
public enum NotificationMethod
{
    Get = 0,
    Post = 1,
}

/// <summary>触发通知的事件集合（PRD 3.5）。位标志，可任意组合。</summary>
[Flags]
public enum NotificationEvents
{
    None = 0,
    BackupStart = 1,
    BackupSuccess = 2,
    BackupFailure = 4,
    RestoreStart = 8,
    RestoreSuccess = 16,
    RestoreFailure = 32,
    CheckStart = 64,
    CheckSuccess = 128,
    CheckFailure = 256,
    UnrecoverableError = 512,
}

/// <summary>
/// 全局通知配置（PRD 4.2）。单例（Id=1）。
/// GET：URL 含 {Title}/{Body} 占位符；POST：另加 body 模板 + content-type。支持代理。
/// </summary>
public class NotificationConfig
{
    public int Id { get; set; }

    public bool Enabled { get; set; }
    public string Url { get; set; } = string.Empty;
    public NotificationMethod Method { get; set; }

    /// <summary>POST 请求体模板（含占位符）。</summary>
    public string? BodyTemplate { get; set; }

    /// <summary>POST content-type（如 application/json、text/plain）。</summary>
    public string? ContentType { get; set; }

    /// <summary>启用的事件（位标志）。</summary>
    public NotificationEvents Events { get; set; }

    /// <summary>代理地址（如 http://host:port）；空则直连。</summary>
    public string? ProxyUrl { get; set; }
}
