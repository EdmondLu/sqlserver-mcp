using SqlServerMcp.Infrastructure;
using SqlServerMcp.Configuration;
using ModelContextProtocol.Protocol;

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

    [Fact]
    public void SuccessResult_UsesStructuredContentWithoutJsonInTextBlock()
    {
        var result = JsonResponse.SuccessResult(
            "probe",
            new { columns = new[] { "id" }, rows = new[] { new { id = 1 } } },
            new SqlServerMcpOptions
            {
                Server = "server,1433",
                Database = "SampleDb",
                CredentialTarget = "credential",
                Connection = new ConnectionOptions { ApplicationIntent = "ReadOnly" }
            },
            "reader",
            12);

        Assert.NotNull(result.StructuredContent);
        Assert.Equal("SampleDb", result.StructuredContent.Value
            .GetProperty("connectionContext")
            .GetProperty("database")
            .GetString());
        Assert.False(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.DoesNotContain("\\\"", text.Text, StringComparison.Ordinal);
        Assert.Contains("structuredContent", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessResult_AllowsStructuredSummaryObject()
    {
        var result = JsonResponse.SuccessResult(
            "plan",
            new { summary = new { risks = Array.Empty<object>() } },
            new SqlServerMcpOptions
            {
                Server = "server",
                Database = "db",
                CredentialTarget = "credential"
            },
            null,
            1);

        Assert.NotNull(result.StructuredContent);
        Assert.False(result.IsError);
    }
}
