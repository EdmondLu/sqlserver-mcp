namespace SqlServerMcp.Infrastructure;

public static class ErrorCodes
{
    public const string ConfigNotFound = "CONFIG_NOT_FOUND";
    public const string ConfigInvalid = "CONFIG_INVALID";
    public const string CredentialNotFound = "CREDENTIAL_NOT_FOUND";
    public const string CredentialReadFailed = "CREDENTIAL_READ_FAILED";
    public const string SqlConnectionFailed = "SQL_CONNECTION_FAILED";
    public const string SqlTimeout = "SQL_TIMEOUT";
    public const string SqlLockTimeout = "SQL_LOCK_TIMEOUT";
    public const string SqlGuardRejected = "SQL_GUARD_REJECTED";
    public const string SqlParseFailed = "SQL_PARSE_FAILED";
    public const string ObjectNotFound = "OBJECT_NOT_FOUND";
    public const string ColumnNotFound = "COLUMN_NOT_FOUND";
    public const string ModuleDefinitionNotAvailable = "MODULE_DEFINITION_NOT_AVAILABLE";
    public const string ViewDefinitionPermissionRequired = "VIEW_DEFINITION_PERMISSION_REQUIRED";
    public const string ShowplanPermissionRequired = "SHOWPLAN_PERMISSION_REQUIRED";
    public const string ResultTooLarge = "RESULT_TOO_LARGE";
    public const string UnknownError = "UNKNOWN_ERROR";
}
