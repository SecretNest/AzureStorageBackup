namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Local validation of Azure Blob container naming rules.
///
/// It exists for the error message: Azure answers an invalid name with "The specifed resource name contains
/// invalid characters.", naming neither the character nor the rule, leaving the user to guess. Checking
/// locally before reaching the cloud allows an actionable explanation instead.
/// </summary>
public static class ContainerName
{
    /// <summary>Returns <c>null</c> when valid, or a sentence of explanation when not (used directly as the API's error text).</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length is < 3 or > 63)
            return "Container name must be between 3 and 63 characters long.";

        foreach (var c in name)
            if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-'))
                return "Container name may only contain lowercase letters, digits, and hyphens.";

        if (name[0] == '-' || name[^1] == '-')
            return "Container name must begin and end with a letter or a digit.";

        if (name.Contains("--", StringComparison.Ordinal))
            return "Container name may not contain consecutive hyphens.";

        return null;
    }
}
