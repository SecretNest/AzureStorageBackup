namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The identity of a storage account for matching purposes: its endpoint, trailing slash dropped, lower-cased.
/// Used both to reject a second account on the same endpoint and to match a settings file's accounts to the
/// stored ones on import — one rule, so an import can never create what a manual add would have refused.
/// </summary>
public static class BlobEndpointKey
{
    public static string Normalize(string endpoint) => endpoint.TrimEnd('/').ToLowerInvariant();
}
