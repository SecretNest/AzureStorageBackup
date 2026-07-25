using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

public class AuthEndpointsTests
{
    private sealed record AuthStatus(bool Required, bool Authenticated);

    /// <summary>启用认证的测试主机；password 为 null 表示不设密码。</summary>
    private static TestWebAppFactory Factory(string? password) =>
        password is null
            ? new TestWebAppFactory()
            : new AuthTestWebAppFactory(password);

    private sealed class AuthTestWebAppFactory(string password) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:Password", password);
        }
    }

    /// <summary>保留 cookie（跨请求自动携带）的客户端；重定向策略用 HttpClient 默认值（跟随）。</summary>
    private static HttpClient Client(WebApplicationFactory<Program> f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    [Fact]
    public async Task Without_A_Password_Everything_Is_Open()
    {
        using var factory = Factory(null);
        var client = Client(factory);

        var status = await client.GetFromJsonAsync<AuthStatus>("/api/auth/status");
        Assert.False(status!.Required);

        var accounts = await client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.OK, accounts.StatusCode);
    }

    [Fact]
    public async Task Without_A_Password_Login_Is_A_No_Op()
    {
        // 认证关闭时没有注册任何 scheme；:20-21 的 gate.Required 短路要是被删掉，
        // SignInAsync 会抛 InvalidOperationException 变成 500。
        using var factory = Factory(null);
        var client = Client(factory);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { password = "anything" });

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    [Fact]
    public async Task Without_A_Password_Logout_Is_A_No_Op()
    {
        // 同上，:41 的 gate.Required 短路守着 SignOutAsync。
        using var factory = Factory(null);
        var client = Client(factory);

        var logout = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
    }

    [Fact]
    public async Task With_A_Password_Api_Requires_Authentication()
    {
        using var factory = Factory("s3cret");
        var client = Client(factory);

        var accounts = await client.GetAsync("/api/accounts");

        Assert.Equal(HttpStatusCode.Unauthorized, accounts.StatusCode);
    }

    [Fact]
    public async Task Status_Is_Reachable_Without_Authentication()
    {
        using var factory = Factory("s3cret");
        var client = Client(factory);

        var res = await client.GetAsync("/api/auth/status");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var status = await res.Content.ReadFromJsonAsync<AuthStatus>();
        Assert.True(status!.Required);
        Assert.False(status.Authenticated);
    }

    [Fact]
    public async Task Health_Probes_Stay_Open_When_A_Password_Is_Set()
    {
        // 探针被挡住会让 docker healthcheck 判定容器不健康并反复重启
        using var factory = Factory("s3cret");
        var client = Client(factory);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Spa_Fallback_Stays_Open_When_A_Password_Is_Set()
    {
        // 挡住 index.html 会让登录页根本渲染不出来——「要登录得先登录」
        using var factory = Factory("s3cret");
        var client = Client(factory);

        var res = await client.GetAsync("/");

        Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Correct_Password_Grants_Access_To_The_Api()
    {
        using var factory = Factory("s3cret");
        var client = Client(factory);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { password = "s3cret" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        var accounts = await client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.OK, accounts.StatusCode);

        var status = await client.GetFromJsonAsync<AuthStatus>("/api/auth/status");
        Assert.True(status!.Authenticated);
    }

    [Fact]
    public async Task Correct_Password_Issues_A_Persistent_Cookie()
    {
        // 设计 §1 决策 7/§3：30 天滑动会话。Program.cs 的 ExpireTimeSpan/SlidingExpiration
        // 只管服务端票据；没有 IsPersistent=true，Set-Cookie 就不带 Max-Age/Expires，
        // 浏览器关闭即丢 cookie——配置在骗人。
        using var factory = Factory("s3cret");
        // 不用 HandleCookies 客户端：那样看不到原始 Set-Cookie 头。
        var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { password = "s3cret" });

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.True(login.Headers.TryGetValues("Set-Cookie", out var setCookieValues));
        var setCookie = Assert.Single(setCookieValues!);
        Assert.True(
            setCookie.Contains("max-age", StringComparison.OrdinalIgnoreCase)
                || setCookie.Contains("expires", StringComparison.OrdinalIgnoreCase),
            $"expected a persistence directive in Set-Cookie, got: {setCookie}");
    }

    [Fact]
    public async Task Wrong_Password_Is_Rejected_And_Grants_Nothing()
    {
        using var factory = Factory("s3cret");
        var client = Client(factory);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        var accounts = await client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, accounts.StatusCode);
    }

    [Fact]
    public async Task Logout_Revokes_Access()
    {
        using var factory = Factory("s3cret");
        var client = Client(factory);
        await client.PostAsJsonAsync("/api/auth/login", new { password = "s3cret" });

        var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var accounts = await client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, accounts.StatusCode);
    }

    [Fact]
    public async Task Login_Works_While_The_Keyring_Is_Lost()
    {
        // 设计 §5：密码比对读环境变量明文、不经密钥环，所以密钥环丢失时仍能登录，
        // 进而走恢复流程。若登录反过来依赖密钥环，就成了「要恢复得先登录，要登录得先恢复」。
        using var factory = Factory("s3cret");
        var client = Client(factory);
        factory.Services.GetRequiredService<IKeyringHealth>().Set(KeyringStatus.Lost);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { password = "s3cret" });

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        // 登录成功后应当能看到恢复所需的状态端点
        var keyring = await client.GetAsync("/api/system/keyring");
        Assert.Equal(HttpStatusCode.OK, keyring.StatusCode);
    }
}
