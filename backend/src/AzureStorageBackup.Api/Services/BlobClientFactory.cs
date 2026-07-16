using System.Net;
using Azure.Core.Pipeline;
using Azure.Storage;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

public class BlobClientFactory : IBlobClientFactory
{
    public BlobServiceClient CreateServiceClient(Account account)
    {
        var uri = new Uri(account.BlobEndpoint);
        var accountName = uri.Host.Split('.')[0];
        var credential = new StorageSharedKeyCredential(accountName, account.AccountKey);

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

    /// <summary>根据账户代理设置构造 HttpClientHandler（公开以便单元测试）。</summary>
    public static HttpClientHandler CreateProxyHandler(Account account)
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
            // 继承 docker/系统环境变量（HTTP_PROXY / HTTPS_PROXY）
            handler.Proxy = HttpClient.DefaultProxy;
        }
        else
        {
            var proxy = new WebProxy($"http://{account.ProxyHost}:{account.ProxyPort}");
            if (!string.IsNullOrEmpty(account.ProxyUsername))
                proxy.Credentials = new NetworkCredential(account.ProxyUsername, account.ProxyPassword);
            handler.Proxy = proxy;
        }

        return handler;
    }
}
