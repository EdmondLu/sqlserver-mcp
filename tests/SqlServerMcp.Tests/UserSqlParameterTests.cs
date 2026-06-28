using SqlServerMcp.Infrastructure;
using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class UserSqlParameterTests
{
    [Fact]
    public void BuildUserSqlParameters_NormalizesNamesAndTypes()
    {
        var parameters = SqlMetadataService.BuildUserSqlParameters(new Dictionary<string, object?>
        {
            ["id"] = 42,
            ["@name"] = "Ada",
            ["isActive"] = true,
            ["missing"] = null
        });

        Assert.Collection(
            parameters,
            parameter =>
            {
                Assert.Equal("@id", parameter.Name);
                Assert.Equal("int", parameter.Definition.Split(' ', 2)[1]);
            },
            parameter =>
            {
                Assert.Equal("@name", parameter.Name);
                Assert.Equal("nvarchar(4000)", parameter.Definition.Split(' ', 2)[1]);
            },
            parameter =>
            {
                Assert.Equal("@isActive", parameter.Name);
                Assert.Equal("bit", parameter.Definition.Split(' ', 2)[1]);
            },
            parameter =>
            {
                Assert.Equal("@missing", parameter.Name);
                Assert.Equal("nvarchar(4000)", parameter.Definition.Split(' ', 2)[1]);
            });
    }

    [Theory]
    [InlineData("")]
    [InlineData("1id")]
    [InlineData("bad-name")]
    [InlineData("dbo.id")]
    public void BuildUserSqlParameters_RejectsInvalidNames(string name)
    {
        var ex = Assert.Throws<SqlMcpException>(() => SqlMetadataService.BuildUserSqlParameters(
            new Dictionary<string, object?> { [name] = 1 }));

        Assert.Equal(ErrorCodes.ConfigInvalid, ex.ErrorCode);
    }
}
