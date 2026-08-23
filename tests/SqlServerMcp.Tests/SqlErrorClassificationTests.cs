using SqlServerMcp.Infrastructure;
using SqlServerMcp.Tools;

namespace SqlServerMcp.Tests;

public sealed class SqlErrorClassificationTests
{
    [Theory]
    [InlineData(208, ErrorCodes.SqlInvalidObject)]
    [InlineData(207, ErrorCodes.SqlInvalidColumn)]
    [InlineData(2715, ErrorCodes.SqlInvalidType)]
    [InlineData(300, ErrorCodes.SqlServerPermissionRequired)]
    [InlineData(8144, ErrorCodes.SqlParameterMismatch)]
    [InlineData(102, ErrorCodes.SqlSyntaxError)]
    [InlineData(50000, ErrorCodes.UnknownError)]
    public void ClassifySqlError_ReturnsActionableCode(int number, string expected)
    {
        Assert.Equal(expected, SqlServerToolService.ClassifySqlError(number));
    }
}
