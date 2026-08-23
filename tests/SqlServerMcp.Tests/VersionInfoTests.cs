using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class VersionInfoTests
{
    [Fact]
    public void GetServerVersion_ReturnsAssemblyVersion()
    {
        var expected = typeof(SqlMetadataService).Assembly.GetName().Version?.ToString();

        Assert.NotNull(expected);
        Assert.Equal(expected, SqlMetadataService.GetServerVersion());
        Assert.Equal("2.3.0.0", expected);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", SqlMetadataService.GetServerVersion());
    }
}
