using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Tests;

public sealed class SqlConnectionFactoryTests
{
    [Fact]
    public void Constructor_DoesNotReadCredential()
    {
        var credentialReader = new FakeCredentialReader();

        _ = new SqlConnectionFactory(CreateOptions(), credentialReader);

        Assert.Equal(0, credentialReader.ReadCount);
    }

    [Fact]
    public async Task GetConnectionString_CachesCredentialUntilReload()
    {
        var credentialReader = new FakeCredentialReader();
        var factory = new SqlConnectionFactory(CreateOptions(), credentialReader);

        var first = await factory.GetConnectionStringAsync(CancellationToken.None);
        var second = await factory.GetConnectionStringAsync(CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(1, credentialReader.ReadCount);
        Assert.Equal(1, factory.CredentialReadCount);

        factory.Reload();
        var third = await factory.GetConnectionStringAsync(CancellationToken.None);

        Assert.Equal(first, third);
        Assert.Equal(2, credentialReader.ReadCount);
        Assert.Equal(2, factory.CredentialReadCount);
    }

    private static SqlServerMcpOptions CreateOptions()
    {
        return new SqlServerMcpOptions
        {
            Server = "localhost,1433",
            Database = "SampleDb",
            CredentialTarget = "sqlserver-mcp/SampleDb",
            Limits = new LimitOptions { ConnectTimeoutSeconds = 5 },
            Connection = new ConnectionOptions
            {
                Encrypt = false,
                TrustServerCertificate = true,
                ApplicationIntent = "ReadOnly"
            }
        };
    }

    private sealed class FakeCredentialReader : IWindowsCredentialReader
    {
        public int ReadCount { get; private set; }

        public WindowsCredential ReadGenericCredential(string target)
        {
            ReadCount++;
            Assert.Equal("sqlserver-mcp/SampleDb", target);
            return new WindowsCredential("readonly_user", "fake-password");
        }
    }
}
