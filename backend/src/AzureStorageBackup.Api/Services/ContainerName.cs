namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Azure Blob container 命名规则的本地校验。
///
/// 存在的理由是错误消息：Azure 对非法名回的是 "The specifed resource name contains
/// invalid characters."，既不指出是哪个字符、也不说明规则，用户看到只能瞎猜。在连云之前
/// 自己判一次，就能给出可操作的说明。
/// </summary>
public static class ContainerName
{
    /// <summary>合法返回 <c>null</c>；非法返回一句英文说明（直接作为 API 的 error 文案）。</summary>
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
