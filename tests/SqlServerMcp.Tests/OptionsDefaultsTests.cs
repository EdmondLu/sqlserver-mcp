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
}
