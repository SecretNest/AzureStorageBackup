namespace AzureStorageBackup.Api.Models;

/// <summary>
/// The key ring health canary (a single row). It stores the ciphertext of a known constant,
/// **bypassing every ValueConverter**, with KeyringProbe calling Protect/Unprotect explicitly (design §3.2).
/// </summary>
public class KeyringCanary
{
    public int Id { get; set; }
    public string Ciphertext { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
