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

    // Validate returns string?, and passing it straight to Assert.Contains raises a nullability warning;
    // asserting non-null first lets the compiler narrow it to string.
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

    // The frontend duplicates this validator in TypeScript. The rule order has to be pinned, because when
    // several rules are violated at once the message that comes back follows a fixed precedence:
    // length > character set > first and last > consecutive hyphens.
    // This test keeps a later refactor or the frontend implementation from changing that precedence by
    // accident.
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
