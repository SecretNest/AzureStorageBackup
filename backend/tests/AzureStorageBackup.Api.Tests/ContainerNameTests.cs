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

    // 前端将来会复制这个验证器到 TypeScript。需要锁定规则顺序，因为多条规则同时违反时
    // 返回的错误消息是有顺序的：长度 > 字符集 > 首尾 > 连续连字符。
    // 这个测试确保后续的重构或前端实现不会无意间改变优先级。
    [Fact]
    public void Precedence_When_Several_Rules_Are_Violated()
    {
        // Violates both character set (uppercase) and begins/ends (hyphens)
        // → character set check comes first, so expect that message.
        AssertRejected("-ABC-", "lowercase letters, digits, and hyphens");

        // Violates both begins/ends (trailing hyphen) and consecutive hyphens
        // → begins/ends check comes first, so expect that message.
        AssertRejected("a--", "begin and end");

        // Violates both length (2 < 3) and begins/ends (starts with hyphen)
        // → length check comes first, so expect that message.
        AssertRejected("-a", "3 and 63");
    }
}
