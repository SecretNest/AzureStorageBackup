using System.Net;
using Azure.Core.Pipeline;
using Azure.Storage;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>Account credentials (the key and the proxy password) are decrypted here — the single chokepoint for every cloud call (design §3.1).</summary>
public class BlobClientFactory(ISecretReader secrets) : IBlobClientFactory
{
    /// <summary>
    /// Parse the account name out of the endpoint: host-style (account.blob.core.windows.net) takes the
    /// first host segment; path-style (Azurite and similar, http://127.0.0.1:10000/account) takes the first
    /// path segment.
    /// </summary>
    public static string ParseAccountName(Uri uri)
    {
        var isPathStyle = IPAddress.TryParse(uri.Host, out _) ||
                          uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (isPathStyle && uri.Segments.Length > 1)
            return uri.Segments[1].Trim('/');
        return uri.Host.Split('.')[0];
    }

    public BlobServiceClient CreateServiceClient(Account account)
    {
        var uri = new Uri(account.BlobEndpoint);
        var accountName = ParseAccountName(uri);
        var credential = new StorageSharedKeyCredential(accountName, secrets.RevealAccountKey(account));

        return new BlobServiceClient(uri, credential, CreateOptions(CreateProxyHandler(account)));
    }

    /// <summary>
    /// How long one attempt at a single request may take before the SDK abandons it and retries.
    /// <para>
    /// The default is 100 seconds, and it is meant for the request sizes a web app makes, not for a backup pushing
    /// volumes up a home uplink. This project measured 4-6 MB/s as the ceiling of one TCP connection to Azure, so
    /// the default volume of 100 MB already spends 17-25 seconds of that budget on a *good* day; an evening where
    /// the line drops to a quarter of that is enough to run a perfectly healthy upload into the timeout, and every
    /// retry then hits the same wall, since a Put Blob starts from zero each time. Five minutes carries a 100 MB
    /// volume down to roughly 0.33 MB/s before it gives up.
    /// </para>
    /// <para>
    /// It is not set higher than that on purpose: this timeout is also the only thing that notices a connection
    /// which has gone silent without being closed, and the retry above it cannot start until this one fires.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Builds the client options. Separate from <see cref="CreateServiceClient"/> so the settings can be asserted
    /// without a live account — they are invisible from a constructed <c>BlobServiceClient</c>, so a test that went
    /// through the public API could only observe them by timing out for real.
    /// </summary>
    internal static BlobClientOptions CreateOptions(HttpMessageHandler handler)
    {
        var options = new BlobClientOptions { Transport = new HttpClientTransport(CreateHttpClient(handler)) };
        options.Retry.NetworkTimeout = NetworkTimeout;
        return options;
    }

    /// <summary>
    /// The transport's own timeout is disabled so that <see cref="NetworkTimeout"/> is the only one in force.
    /// <para>
    /// <see cref="HttpClient.Timeout"/> covers a whole request and knows nothing about the SDK's retries, so
    /// whichever of the two is smaller silently becomes the real limit. Left at its 100-second default it would make
    /// the setting above pure decoration — the SDK would never get to apply it, and the failure would keep arriving
    /// as a TaskCanceledException naming a timeout nobody in this codebase had chosen.
    /// </para>
    /// </summary>
    internal static HttpClient CreateHttpClient(HttpMessageHandler handler)
        => new(handler) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<ConnectionResult> TestConnectionAsync(Account account, CancellationToken ct = default)
    {
        try
        {
            var client = CreateServiceClient(account);
            await client.GetPropertiesAsync(ct);
            return new ConnectionResult(true, null);
        }
        catch (Exception ex)
        {
            return new ConnectionResult(false, ex.Message);
        }
    }

    /// <summary>Build an HttpClientHandler from the account's proxy settings (public so it can be unit-tested).</summary>
    public HttpClientHandler CreateProxyHandler(Account account)
    {
        var handler = new HttpClientHandler();

        if (!account.UseProxy)
        {
            handler.UseProxy = false;
            return handler;
        }

        handler.UseProxy = true;

        if (account.ProxyMode == ProxyMode.DockerEnv)
        {
            // Inherit from the docker/system environment variables (HTTP_PROXY / HTTPS_PROXY)
            handler.Proxy = HttpClient.DefaultProxy;
        }
        else
        {
            var proxy = new WebProxy($"http://{account.ProxyHost}:{account.ProxyPort}");
            if (!string.IsNullOrEmpty(account.ProxyUsername))
                proxy.Credentials = new NetworkCredential(
                    account.ProxyUsername, secrets.RevealProxyPassword(account));
            handler.Proxy = proxy;
        }

        return handler;
    }
}
