namespace SqlServerMcp.Infrastructure;

public sealed class SqlMcpException : Exception
{
    public SqlMcpException(string errorCode, string message, string? detail = null, string? hint = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Detail = detail;
        Hint = hint;
    }

    public string ErrorCode { get; }

    public string? Detail { get; }

    public string? Hint { get; }
}
