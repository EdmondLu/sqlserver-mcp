using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Tests;

public sealed class JsonResponseTests
{
    [Fact]
    public void Success_PreservesDecimalText()
    {
        var json = JsonResponse.Success(new { amount = 123.4500m });

        Assert.Contains("123.4500", json);
    }

    [Fact]
    public void Error_UsesStructuredShape()
    {
        var json = JsonResponse.Error(ErrorCodes.SqlGuardRejected, "Rejected.", "detail", "hint");

        Assert.Contains("\"ok\": false", json);
        Assert.Contains(ErrorCodes.SqlGuardRejected, json);
        Assert.Contains("detail", json);
        Assert.Contains("hint", json);
    }
}
