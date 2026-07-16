using System.Net;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class BlobClientFactoryTests
{
    private const string Key = "dGVzdGtleQ=="; // base64("testkey")

    private static Account Sample() => new()
    {
        Name = "prod",
        BlobEndpoint = "https://prod.blob.core.windows.net",
        AccountKey = Key,
        Region = AzureRegion.Global
    };

    [Fact]
    public void CreateServiceClient_Uses_Endpoint_And_AccountName()
    {
        var factory = new BlobClientFactory();

        var client = factory.CreateServiceClient(Sample());

        Assert.Equal("prod", client.AccountName);
        Assert.Equal(new Uri("https://prod.blob.core.windows.net"), client.Uri);
    }

    [Fact]
    public void ParseAccountName_HostStyle_Uses_First_Host_Label() =>
        Assert.Equal("prod", BlobClientFactory.ParseAccountName(
            new Uri("https://prod.blob.core.windows.net")));

    [Fact]
    public void ParseAccountName_PathStyle_Ip_Uses_First_Path_Segment() =>
        Assert.Equal("devstoreaccount1", BlobClientFactory.ParseAccountName(
            new Uri("http://127.0.0.1:10000/devstoreaccount1")));

    [Fact]
    public void ParseAccountName_PathStyle_Localhost_Uses_First_Path_Segment() =>
        Assert.Equal("myacct", BlobClientFactory.ParseAccountName(
            new Uri("http://localhost:10000/myacct")));

    [Fact]
    public void ProxyHandler_NoProxy_Disables_Proxy()
    {
        var acct = Sample();
        acct.UseProxy = false;

        var handler = BlobClientFactory.CreateProxyHandler(acct);

        Assert.False(handler.UseProxy);
    }

    [Fact]
    public void ProxyHandler_Independent_Sets_WebProxy_With_Credentials()
    {
        var acct = Sample();
        acct.UseProxy = true;
        acct.ProxyMode = ProxyMode.Independent;
        acct.ProxyHost = "proxy.local";
        acct.ProxyPort = 8080;
        acct.ProxyUsername = "u";
        acct.ProxyPassword = "p";

        var handler = BlobClientFactory.CreateProxyHandler(acct);

        Assert.True(handler.UseProxy);
        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal(new Uri("http://proxy.local:8080"), proxy.Address);
        var cred = Assert.IsType<NetworkCredential>(proxy.Credentials);
        Assert.Equal("u", cred.UserName);
        Assert.Equal("p", cred.Password);
    }

    [Fact]
    public void ProxyHandler_Independent_NoCredentials_Leaves_Credentials_Null()
    {
        var acct = Sample();
        acct.UseProxy = true;
        acct.ProxyMode = ProxyMode.Independent;
        acct.ProxyHost = "proxy.local";
        acct.ProxyPort = 3128;

        var handler = BlobClientFactory.CreateProxyHandler(acct);

        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Null(proxy.Credentials);
    }

    [Fact]
    public void ProxyHandler_DockerEnv_Uses_Default_Proxy()
    {
        var acct = Sample();
        acct.UseProxy = true;
        acct.ProxyMode = ProxyMode.DockerEnv;

        var handler = BlobClientFactory.CreateProxyHandler(acct);

        Assert.True(handler.UseProxy);
        Assert.NotNull(handler.Proxy);
    }
}
