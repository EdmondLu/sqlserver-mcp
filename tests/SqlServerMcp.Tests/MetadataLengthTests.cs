using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class MetadataLengthTests
{
    [Theory]
    [InlineData(8, "bigint")]
    [InlineData(4, "int")]
    [InlineData(16, "uniqueidentifier")]
    [InlineData(8, "datetime2")]
    [InlineData(32, "varbinary")]
    public void NormalizeMaxLengthCharacters_ReturnsNullForNonCharacterTypes(short maxLength, string dataType)
    {
        Assert.Null(SqlMetadataService.NormalizeMaxLengthCharacters(maxLength, dataType));
    }

    [Theory]
    [InlineData(200, "nvarchar", 100)]
    [InlineData(100, "varchar", 100)]
    [InlineData(-1, "nvarchar", -1)]
    [InlineData(-1, "varchar", -1)]
    public void NormalizeMaxLengthCharacters_ReturnsDeclaredCharacterCapacity(
        short maxLength,
        string dataType,
        int expected)
    {
        Assert.Equal(expected, SqlMetadataService.NormalizeMaxLengthCharacters(maxLength, dataType));
    }

    [Theory]
    [InlineData(8, "bigint")]
    [InlineData(17, "decimal(18,2)")]
    [InlineData(16, "uniqueidentifier")]
    public void NormalizeResultMaxLengthCharacters_ReturnsNullForNonCharacterTypes(
        int maxLength,
        string systemTypeName)
    {
        Assert.Null(SqlMetadataService.NormalizeResultMaxLengthCharacters(maxLength, systemTypeName));
    }

    [Theory]
    [InlineData(200, "nvarchar(100)", 100)]
    [InlineData(100, "varchar(100)", 100)]
    [InlineData(-1, "nvarchar(max)", -1)]
    public void NormalizeResultMaxLengthCharacters_ReturnsCharacterCapacity(
        int maxLength,
        string systemTypeName,
        int expected)
    {
        Assert.Equal(expected, SqlMetadataService.NormalizeResultMaxLengthCharacters(maxLength, systemTypeName));
    }
}
