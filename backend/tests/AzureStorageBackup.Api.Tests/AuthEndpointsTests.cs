using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

public class AuthEndpointsTests
{
    private sealed record AuthStatus(bool Required, bool Authenticated);

    /// <summary>Test host with authentication enabled; a null password means no password is configured.</summary>
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

    /// <summary>Client that keeps cookies (carried automatically across requests); redirect policy stays at the HttpClient default (follow).</summary>
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
        // With authentication off no scheme is registered; delete the gate.Required short-circuit at :20-21 and
        // SignInAsync throws InvalidOperationException, which surfaces as a 500.
        using var factory = Factory(null);
        var client = Client(factory);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { password = "anything" });

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    [Fact]
    public async Task Without_A_Password_Logout_Is_A_No_Op()
    {
        // Same as above; the gate.Required short-circuit at :41 guards SignOutAsync.
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
        // Blocking the probes makes the docker healthcheck declare the container unhealthy and restart it over and over
        using var factory = Factory("s3cret");
        var client = Client(factory);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Spa_Fallback_Stays_Open_When_A_Password_Is_Set()
    {
        // Blocking index.html means the login page never renders at all — "to log in, first log in"
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
        // Design §1 decision 7/§3: a 30-day sliding session. The ExpireTimeSpan/SlidingExpiration in Program.cs
        // only govern the server-side ticket; without IsPersistent=true the Set-Cookie carries no Max-Age/Expires,
        // so the cookie dies when the browser closes — the configuration is lying.
        using var factory = Factory("s3cret");
        // Not the HandleCookies client: that one hides the raw Set-Cookie header.
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
        // Design §5: the password comparison reads the environment variable in plaintext and never touches the keyring, so you
        // can still log in — and therefore run the recovery flow — while the keyring is lost. If login depended on the keyring instead, you would get "to recover you must log in, to log in you must first recover".
        using var factory = Factory("s3cret");
        var client = Client(factory);
        factory.Services.GetRequiredService<IKeyringHealth>().Set(KeyringStatus.Lost);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { password = "s3cret" });

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        // Once logged in, the status endpoint the recovery flow needs must be visible
        var keyring = await client.GetAsync("/api/system/keyring");
        Assert.Equal(HttpStatusCode.OK, keyring.StatusCode);
    }

    [Fact]
    public async Task Concurrent_Failed_Logins_Are_Serialized()
    {
        // A per-request "sleep 1 second after a failure" does not stop concurrent brute force: with N requests in flight
        // at once the cost per attempt approaches 0, and design §4.3's "make online brute force not worth it" is not delivered.
        // With the failure path serialized globally, 3 concurrent failures must burn about 3 seconds of wall clock.
        const int attempts = 3;
        using var factory = Factory("s3cret");
        var client = Client(factory);

        var sw = Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(0, attempts).Select(_ =>
            client.PostAsJsonAsync("/api/auth/login", new { password = "wrong" })));
        sw.Stop();

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        // Serialized = each failure takes its own full second; 0.5 s of slack absorbs scheduling jitter.
        Assert.True(
            sw.Elapsed >= TimeSpan.FromMilliseconds(1000 * attempts - 500),
            $"{attempts} concurrent failed logins took {sw.ElapsedMilliseconds} ms; "
                + "the failure delay is not serialized, so parallel brute force pays almost nothing.");
    }

    [Fact]
    public async Task Ready_Probe_Hides_Component_Detail_From_Anonymous_Callers()
    {
        // The probe must be reachable anonymously, but an anonymous caller must not be able to read "this box is in keyring recovery mode" out of it.
        using var factory = Factory("s3cret");
        var client = Client(factory);

        var anonymous = await client.GetAsync("/api/health/ready");
        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode); // the status code is the only thing probes consume; it must not change
        var anonymousBody = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ready", anonymousBody.GetProperty("status").GetString());
        Assert.False(anonymousBody.TryGetProperty("database", out _));
        Assert.False(anonymousBody.TryGetProperty("keyring", out _));

        await client.PostAsJsonAsync("/api/auth/login", new { password = "s3cret" });

        var authenticated = await client.GetAsync("/api/health/ready");
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        var authenticatedBody = JsonDocument.Parse(await authenticated.Content.ReadAsStringAsync()).RootElement;
        Assert.True(authenticatedBody.GetProperty("database").GetBoolean());
        Assert.True(authenticatedBody.GetProperty("keyring").GetBoolean());
    }

    [Fact]
    public async Task Ready_Probe_Keeps_Its_Detail_When_Authentication_Is_Disabled()
    {
        // With no password set, the behaviour must be exactly what it was before this round of changes
        using var factory = Factory(null);
        var client = Client(factory);

        var response = await client.GetAsync("/api/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("database").GetBoolean());
        Assert.True(body.GetProperty("keyring").GetBoolean());
    }
}
