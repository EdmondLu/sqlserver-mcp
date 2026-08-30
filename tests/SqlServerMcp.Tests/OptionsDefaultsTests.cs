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

    [Fact]
    public void TextSearchOptions_HaveNoTargetsByDefault()
    {
        var options = new TextSearchOptions();

        Assert.Empty(options.Targets);
        Assert.Equal(240, options.SnippetLength);
    }

    [Fact]
    public void CompareOptions_NormalizeRepoExcludePatterns()
    {
        var options = new CompareOptions
        {
            RepoExcludePatterns = [" backup\\** ", "backup/**", "", "domain2/**"]
        };

        options.Normalize();

        Assert.Equal(["backup/**", "domain2/**"], options.RepoExcludePatterns);
    }

    [Fact]
    public void LimitOptions_ProvideBoundedLobDefaults()
    {
        var options = new LimitOptions();

        Assert.Equal(50, options.MaxLobMb);
        Assert.Equal(262_144, options.MaxLobChunkSize);

        options.MaxLobMb = int.MaxValue;
        options.MaxLobChunkSize = int.MaxValue;
        options.Normalize();

        Assert.Equal(512, options.MaxLobMb);
        Assert.Equal(1_048_576, options.MaxLobChunkSize);
    }
}
