namespace AzureStorageBackup.Api.Models;

public record NotificationResponse(
    bool Enabled,
    string Url,
    NotificationMethod Method,
    string? BodyTemplate,
    string? ContentType,
    NotificationEvents Events,
    string? ProxyUrl)
{
    public static NotificationResponse From(NotificationConfig c) =>
        new(c.Enabled, c.Url, c.Method, c.BodyTemplate, c.ContentType, c.Events, c.ProxyUrl);
}

public record NotificationRequest(
    bool Enabled,
    string Url,
    NotificationMethod Method,
    string? BodyTemplate,
    string? ContentType,
    NotificationEvents Events,
    string? ProxyUrl)
{
    public NotificationConfig ToConfig() => new()
    {
        Enabled = Enabled,
        Url = Url,
        Method = Method,
        BodyTemplate = BodyTemplate,
        ContentType = ContentType,
        Events = Events,
        ProxyUrl = ProxyUrl,
    };
}
