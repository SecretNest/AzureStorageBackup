using System.Net;
using Azure.Core.Pipeline;
using Azure.Storage;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>账户凭据（key/代理密码）在此解密——云端调用的唯一咽喉（设计 §3.1）。</summary>
public class BlobClientFactory(ISecretReader secrets) : IBlobClientFactory
{
    /// <summary>
    /// 从 endpoint 解析账户名：host-style（account.blob.core.windows.net）取 host 首段；
    /// path-style（Azurite 等，http://127.0.0.1:10000/account）取首个路径段。
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

    /// <summary>根据账户代理设置构造 HttpClientHandler（公开以便单元测试）。</summary>
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
            // 继承 docker/系统环境变量（HTTP_PROXY / HTTPS_PROXY）
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
