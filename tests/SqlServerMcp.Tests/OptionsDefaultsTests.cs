using SqlServerMcp.Configuration;

namespace SqlServerMcp.Tests;

public sealed class OptionsDefaultsTests
{
    [Fact]
    public void ConnectionOptions_UseSecureDefaults()
    {
        var options = new ConnectionOptions();

        Assert.True(options.Encrypt);
        Assert.False(options.TrustServerCertificate);
        Assert.Equal("ReadOnly", options.ApplicationIntent);
    }

    [Fact]
    public void LoggingOptions_DoNotLogSqlByDefault()
    {
        var options = new LoggingOptions();

        Assert.False(options.LogSql);
    }
}
