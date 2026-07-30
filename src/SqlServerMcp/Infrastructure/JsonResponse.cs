using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using SqlServerMcp.Configuration;

namespace SqlServerMcp.Infrastructure;

public static class JsonResponse
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Success(object result)
    {
        return JsonSerializer.Serialize(result, Options);
    }

    public static string Error(string errorCode, string message, string? detail = null, string? hint = null)
    {
        return JsonSerializer.Serialize(
            new
            {
                ok = false,
                errorCode,
                message,
                detail,
                hint
            },
            Options);
    }

    public static CallToolResult SuccessResult(
        string toolName,
        object result,
        SqlServerMcpOptions options,
        string? login,
        long elapsedMs)
    {
        var payload = AddConnectionContext(result, options, login, elapsedMs);
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = BuildTextSummary(toolName, payload, isError: false)
                }
            ],
            StructuredContent = JsonSerializer.SerializeToElement(payload, Options),
            IsError = false
        };
    }

    public static CallToolResult ErrorResult(
        string toolName,
        string errorCode,
        string message,
        SqlServerMcpOptions options,
        string? login,
        long elapsedMs,
        string? detail = null,
        string? hint = null,
        int? sqlErrorNumber = null,
        int? lineNumber = null,
        IReadOnlyList<string>? suggestions = null)
    {
        var error = new
        {
            ok = false,
            errorCode,
            sqlErrorNumber,
            message,
            lineNumber,
            detail,
            hint,
            suggestions = suggestions ?? []
        };
        var payload = AddConnectionContext(error, options, login, elapsedMs);
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = BuildTextSummary(toolName, payload, isError: true)
                }
            ],
            StructuredContent = JsonSerializer.SerializeToElement(payload, Options),
            IsError = true
        };
    }

    private static JsonObject AddConnectionContext(
        object result,
        SqlServerMcpOptions options,
        string? login,
        long elapsedMs)
    {
        var node = JsonSerializer.SerializeToNode(result, Options);
        var payload = node as JsonObject ?? new JsonObject { ["result"] = node };
        payload["connectionContext"] = JsonSerializer.SerializeToNode(
            new
            {
                server = options.Server,
                database = options.Database,
                readOnly = string.Equals(
                    options.Connection.ApplicationIntent,
                    "ReadOnly",
                    StringComparison.OrdinalIgnoreCase),
                login,
                asOfTime = DateTimeOffset.Now,
                elapsedMs,
                isolationLevel = "READ COMMITTED"
            },
            Options);
        return payload;
    }

    private static string BuildTextSummary(string toolName, JsonObject payload, bool isError)
    {
        if (isError)
        {
            var code = payload["errorCode"]?.GetValue<string>() ?? ErrorCodes.UnknownError;
            var message = payload["message"]?.GetValue<string>() ?? "Tool call failed.";
            return $"{toolName} failed: {code}: {message} Structured details are available in structuredContent.";
        }

        var summary = payload["summary"] is JsonValue summaryValue
                      && summaryValue.TryGetValue<string>(out var summaryText)
            ? summaryText
            : null;
        return string.IsNullOrWhiteSpace(summary)
            ? $"{toolName} completed. Structured result is available in structuredContent."
            : $"{toolName}: {summary}";
    }
}
