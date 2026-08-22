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
        AccountKeyProtected = TestSecrets.Protect(Key),
        Region = AzureRegion.Global
    };

    [Fact]
    public void CreateServiceClient_Uses_Endpoint_And_AccountName()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);

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

        var handler = new BlobClientFactory(TestSecrets.Reader).CreateProxyHandler(acct);

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
        acct.ProxyPasswordProtected = TestSecrets.Protect("p");

        var handler = new BlobClientFactory(TestSecrets.Reader).CreateProxyHandler(acct);

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

        var handler = new BlobClientFactory(TestSecrets.Reader).CreateProxyHandler(acct);

        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Null(proxy.Credentials);
    }

    [Fact]
    public void ProxyHandler_DockerEnv_Uses_Default_Proxy()
    {
        var acct = Sample();
        acct.UseProxy = true;
        acct.ProxyMode = ProxyMode.DockerEnv;

        var handler = new BlobClientFactory(TestSecrets.Reader).CreateProxyHandler(acct);

        Assert.True(handler.UseProxy);
        Assert.NotNull(handler.Proxy);
    }

    /// <summary>
    /// The whole point of the cache, and reference equality is the only way to assert it: the connection pool lives
    /// in the handler behind the client, so "the same instance" is the same pool and a new instance is a fresh TCP
    /// connect plus TLS handshake. BlobUploader reaches this once per volume, so before the cache every 100 MB
    /// volume opened its own connection and started its congestion window from zero.
    /// </summary>
    [Fact]
    public void CreateServiceClient_Reuses_One_Client_Per_Account()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var acct = Sample();
        acct.Id = 7;

        Assert.Same(factory.CreateServiceClient(acct), factory.CreateServiceClient(acct));
    }

    /// <summary>
    /// Reuse must not outlive the settings it was built from. The account object handed in is re-read from the
    /// database on each scope, so the cache cannot rely on reference identity to notice an edit — it compares a
    /// fingerprint, and this is the case that says the fingerprint is actually consulted.
    /// </summary>
    [Fact]
    public void CreateServiceClient_Rebuilds_When_The_Endpoint_Changes()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var acct = Sample();
        acct.Id = 7;
        var first = factory.CreateServiceClient(acct);

        acct.BlobEndpoint = "https://other.blob.core.windows.net";
        var second = factory.CreateServiceClient(acct);

        Assert.NotSame(first, second);
        Assert.Equal("other", second.AccountName);
    }

    /// <summary>
    /// A rotated key has to reach the cloud without a restart. The fingerprint is taken over the **ciphertext**, so
    /// this is what proves that choice works: nothing else about the account moved, and the client still has to be
    /// rebuilt or every subsequent request is signed with the key the operator just replaced.
    /// </summary>
    [Fact]
    public void CreateServiceClient_Rebuilds_When_The_Key_Is_Rotated()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var acct = Sample();
        acct.Id = 7;
        var first = factory.CreateServiceClient(acct);

        acct.AccountKeyProtected = TestSecrets.Protect("c2Vjb25ka2V5");
        Assert.NotSame(first, factory.CreateServiceClient(acct));
    }

    /// <summary>Turning a proxy on is a different route to the same endpoint, and the pool behind the old client
    /// does not go through it.</summary>
    [Fact]
    public void CreateServiceClient_Rebuilds_When_The_Proxy_Changes()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var acct = Sample();
        acct.Id = 7;
        var first = factory.CreateServiceClient(acct);

        acct.UseProxy = true;
        acct.ProxyMode = ProxyMode.Independent;
        acct.ProxyHost = "proxy.local";
        acct.ProxyPort = 8080;

        Assert.NotSame(first, factory.CreateServiceClient(acct));
    }

    /// <summary>Two accounts are two pools, however much else they have in common.</summary>
    [Fact]
    public void CreateServiceClient_Keeps_Accounts_Apart()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var one = Sample();
        one.Id = 1;
        var two = Sample();
        two.Id = 2;

        Assert.NotSame(factory.CreateServiceClient(one), factory.CreateServiceClient(two));
    }
}
