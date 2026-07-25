using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.Configuration;

namespace AzureStorageBackup.Api.Tests;

public class AuthGateTests
{
    private static AuthGate Create(string? password)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(password is null
                ? []
                : new Dictionary<string, string?> { ["Auth:Password"] = password })
            .Build();
        return new AuthGate(config);
    }

    [Fact]
    public void Not_Required_When_Password_Is_Absent() => Assert.False(Create(null).Required);

    [Fact]
    public void Not_Required_When_Password_Is_Empty() => Assert.False(Create("").Required);

    [Fact]
    public void Required_When_Password_Is_Set() => Assert.True(Create("s3cret").Required);

    [Fact]
    public void Verify_Accepts_The_Configured_Password()
        => Assert.True(Create("s3cret").Verify("s3cret"));

    [Fact]
    public void Verify_Rejects_A_Wrong_Password()
        => Assert.False(Create("s3cret").Verify("wrong"));

    [Fact]
    public void Verify_Rejects_A_Password_Of_Different_Length()
        => Assert.False(Create("s3cret").Verify("s3cretx"));

    [Fact]
    public void Verify_Rejects_Null_And_Empty()
    {
        var sut = Create("s3cret");
        Assert.False(sut.Verify(null));
        Assert.False(sut.Verify(""));
    }

    [Fact]
    public void Verify_Always_True_When_Not_Required()
    {
        // 认证关闭时不该有任何东西被拒——调用方据此放行
        var sut = Create(null);
        Assert.True(sut.Verify(null));
        Assert.True(sut.Verify("anything"));
    }

    [Fact]
    public void Verify_Handles_Non_Ascii_Passwords()
        => Assert.True(Create("пароль密码").Verify("пароль密码"));
}
