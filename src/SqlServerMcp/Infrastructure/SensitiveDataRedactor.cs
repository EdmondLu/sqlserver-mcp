using System.Text.RegularExpressions;

namespace SqlServerMcp.Infrastructure;

internal static partial class SensitiveDataRedactor
{
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var redacted = ConnectionSecretRegex().Replace(value, match => $"{match.Groups[1].Value}<redacted>");
        redacted = JsonSecretRegex().Replace(redacted, match => $"{match.Groups[1].Value}<redacted>{match.Groups[3].Value}");
        redacted = BearerTokenRegex().Replace(redacted, match => $"{match.Groups[1].Value}<redacted>");
        redacted = QuerySecretRegex().Replace(redacted, match => $"{match.Groups[1].Value}<redacted>");
        return redacted;
    }

    [GeneratedRegex(@"(?i)\b(password|pwd|access[_-]?token|api[_-]?key|client[_-]?secret)\s*=\s*([^;\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionSecretRegex();

    [GeneratedRegex("(?i)([\"'](?:password|pwd|access[_-]?token|api[_-]?key|client[_-]?secret)[\"']\\s*:\\s*[\"'])(.*?)([\"'])", RegexOptions.CultureInvariant)]
    private static partial Regex JsonSecretRegex();

    [GeneratedRegex(@"(?i)\b(authorization\s*:\s*bearer\s+)([^\s,;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?i)([?&](?:token|access_token|api_key|key|sig|signature)=)([^&#\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex QuerySecretRegex();
}
