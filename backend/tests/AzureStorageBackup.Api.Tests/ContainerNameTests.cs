using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class ContainerNameTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("my-backup-2024")]
    [InlineData("a1b")]
    [InlineData("0123456789")]
    public void Accepts_Valid_Names(string name) =>
        Assert.Null(ContainerName.Validate(name));

    // Validate 返回 string?，直接塞进 Assert.Contains 会触发可空警告；
    // 先断言非空，编译器随后把它收窄为 string。
    private static void AssertRejected(string? name, string expectedFragment)
    {
        var message = ContainerName.Validate(name);
        Assert.NotNull(message);
        Assert.Contains(expectedFragment, message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    public void Rejects_Too_Short(string? name) => AssertRejected(name, "3 and 63");

    [Fact]
    public void Rejects_Too_Long() => AssertRejected(new string('a', 64), "3 and 63");

    [Theory]
    [InlineData("MyBackup")]
    [InlineData("my_backup")]
    [InlineData("my.backup")]
    [InlineData("my backup")]
    public void Rejects_Disallowed_Characters(string name) =>
        AssertRejected(name, "lowercase letters, digits, and hyphens");

    [Theory]
    [InlineData("-abc")]
    [InlineData("abc-")]
    public void Rejects_Hyphen_At_Either_End(string name) =>
        AssertRejected(name, "begin and end");

    [Fact]
    public void Rejects_Consecutive_Hyphens() => AssertRejected("a--b", "consecutive");
}
