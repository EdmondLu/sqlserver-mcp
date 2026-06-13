using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class StructureObjectNameMapperTests
{
    [Theory]
    [InlineData("vwp_CustomerOrder", "CustomerOrder")]
    [InlineData("vwpr_CustomerOrder", "CustomerOrder")]
    [InlineData("vwt_CustomerOrder", "CustomerOrder")]
    [InlineData("vwtr_CustomerOrder", "CustomerOrder")]
    [InlineData("VWP_CustomerOrder", "CustomerOrder")]
    public void MapStructureObjectName_StripsKnownViewPrefixes(string name, string expected)
    {
        Assert.Equal(expected, SqlMetadataService.MapStructureObjectName(name));
    }

    [Fact]
    public void MapStructureObjectName_ReturnsNullForPlainObjectName()
    {
        Assert.Null(SqlMetadataService.MapStructureObjectName("CustomerOrder"));
    }
}
