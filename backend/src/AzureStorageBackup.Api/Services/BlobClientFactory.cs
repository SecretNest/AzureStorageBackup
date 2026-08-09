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

        var options = new BlobClientOptions
        {
            Transport = new HttpClientTransport(new HttpClient(CreateProxyHandler(account)))
        };

        return new BlobServiceClient(uri, credential, options);
    }

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
