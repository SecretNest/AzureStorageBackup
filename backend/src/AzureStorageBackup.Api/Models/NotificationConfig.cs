namespace AzureStorageBackup.Api.Models;

/// <summary>Push method (PRD 4.2).</summary>
public enum NotificationMethod
{
    Get = 0,
    Post = 1,
}

/// <summary>The set of events that trigger a notification (PRD 3.5). Bit flags, combinable in any way.</summary>
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
/// Global notification configuration (PRD 4.2). A singleton (Id=1).
/// GET: the URL carries {Title}/{Body} placeholders; POST: additionally a body template + content-type. Proxies are supported.
/// </summary>
public class NotificationConfig
{
    public int Id { get; set; }

    public bool Enabled { get; set; }
    public string Url { get; set; } = string.Empty;
    public NotificationMethod Method { get; set; }

    /// <summary>POST request body template (with placeholders).</summary>
    public string? BodyTemplate { get; set; }

    /// <summary>POST content-type (e.g. application/json, text/plain).</summary>
    public string? ContentType { get; set; }

    /// <summary>Enabled events (bit flags).</summary>
    public NotificationEvents Events { get; set; }

    /// <summary>Proxy address (e.g. http://host:port); empty means connect directly.</summary>
    public string? ProxyUrl { get; set; }
}
