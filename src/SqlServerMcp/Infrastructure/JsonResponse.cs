using System.Text.Encodings.Web;
using System.Text.Json;

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
}
