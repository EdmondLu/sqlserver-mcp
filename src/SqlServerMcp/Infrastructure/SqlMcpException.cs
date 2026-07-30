namespace SqlServerMcp.Infrastructure;

public sealed class SqlMcpException : Exception
{
    public SqlMcpException(
        string errorCode,
        string message,
        string? detail = null,
        string? hint = null,
        Exception? innerException = null,
        int? sqlErrorNumber = null,
        int? lineNumber = null,
        IReadOnlyList<string>? suggestions = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Detail = detail;
        Hint = hint;
        SqlErrorNumber = sqlErrorNumber;
        LineNumber = lineNumber;
        Suggestions = suggestions ?? [];
    }

    public string ErrorCode { get; }

    public string? Detail { get; }

    public string? Hint { get; }

    public int? SqlErrorNumber { get; }

    public int? LineNumber { get; }

    public IReadOnlyList<string> Suggestions { get; }
}
