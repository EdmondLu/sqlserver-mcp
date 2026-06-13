using System.Text.Json;
using System.Text.Json.Serialization;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Configuration;

public sealed class SqlServerMcpOptions
{
    public required string Server { get; init; }

    public required string Database { get; init; }

    public required string CredentialTarget { get; init; }

    public LimitOptions Limits { get; init; } = new();

    public SecurityOptions Security { get; init; } = new();

    public LoggingOptions Logging { get; init; } = new();

    public ConnectionOptions Connection { get; init; } = new();

    public RuntimeOptions Runtime { get; private set; } = new();

    [JsonIgnore]
    public string ConfigPath { get; private set; } = string.Empty;

    public static SqlServerMcpOptions Load(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigNotFound,
                "SQL Server MCP config file was not found.",
                configPath,
                "Create sqlserver_mcp.json or pass --config with an absolute path.");
        }

        var json = File.ReadAllText(configPath);
        var options = JsonSerializer.Deserialize<SqlServerMcpOptions>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

        if (options is null)
        {
            throw new SqlMcpException(ErrorCodes.ConfigInvalid, "Config file is empty or invalid JSON.");
        }

        options.ConfigPath = Path.GetFullPath(configPath);
        options.Runtime = options.Runtime.Resolve(Path.GetDirectoryName(options.ConfigPath)!);
        options.Validate();
        return options;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Server))
        {
            throw new SqlMcpException(ErrorCodes.ConfigInvalid, "Config field 'server' is required.");
        }

        if (string.IsNullOrWhiteSpace(Database))
        {
            throw new SqlMcpException(ErrorCodes.ConfigInvalid, "Config field 'database' is required.");
        }

        if (string.IsNullOrWhiteSpace(CredentialTarget))
        {
            throw new SqlMcpException(ErrorCodes.ConfigInvalid, "Config field 'credentialTarget' is required.");
        }

        if (!Security.AllowSystemDatabases && SecurityOptions.SystemDatabases.Contains(Database))
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                $"Database '{Database}' is blocked by allowSystemDatabases=false.");
        }

        Limits.Normalize();
        Logging.Normalize();
    }
}

public sealed class LimitOptions
{
    public int DefaultLimit { get; set; } = 50;

    public int MaxRows { get; set; } = 500;

    public int MaxResultMb { get; set; } = 5;

    public int MaxTextLength { get; set; } = 1000;

    public int LockTimeoutMs { get; set; } = 5000;

    public int CommandTimeoutSeconds { get; set; } = 20;

    public int ConnectTimeoutSeconds { get; set; } = 10;

    public void Normalize()
    {
        DefaultLimit = Math.Clamp(DefaultLimit, 1, 500);
        MaxRows = Math.Clamp(MaxRows, 1, 5000);
        MaxResultMb = Math.Clamp(MaxResultMb, 1, 100);
        MaxTextLength = Math.Clamp(MaxTextLength, 100, 100_000);
        LockTimeoutMs = Math.Clamp(LockTimeoutMs, 1, 60_000);
        CommandTimeoutSeconds = Math.Clamp(CommandTimeoutSeconds, 1, 300);
        ConnectTimeoutSeconds = Math.Clamp(ConnectTimeoutSeconds, 1, 60);
    }

    public int ClampRows(int? requested)
    {
        if (requested is null or <= 0)
        {
            return Math.Min(DefaultLimit, MaxRows);
        }

        return Math.Clamp(requested.Value, 1, MaxRows);
    }
}

public sealed class SecurityOptions
{
    public static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "master",
        "model",
        "msdb",
        "tempdb"
    };

    public bool AllowDmvQueries { get; set; } = true;

    public bool AllowServerLevelDmv { get; set; }

    public bool AllowCrossDatabase { get; set; }

    public bool AllowSystemDatabases { get; set; }
}

public sealed class LoggingOptions
{
    public bool LogSql { get; set; }

    public int MaxFileSizeMb { get; set; } = 10;

    public int MaxRetainedFiles { get; set; } = 7;

    public void Normalize()
    {
        MaxFileSizeMb = Math.Clamp(MaxFileSizeMb, 1, 100);
        MaxRetainedFiles = Math.Clamp(MaxRetainedFiles, 1, 100);
    }
}

public sealed class ConnectionOptions
{
    public bool Encrypt { get; set; } = true;

    public bool TrustServerCertificate { get; set; }

    public string ApplicationIntent { get; set; } = "ReadOnly";
}

public sealed class RuntimeOptions
{
    public string LogDirectory { get; init; } = "logs";

    public string CacheDirectory { get; init; } = "cache";

    public string TempDirectory { get; init; } = "tmp";

    public RuntimeOptions Resolve(string baseDirectory)
    {
        return new RuntimeOptions
        {
            LogDirectory = ResolvePath(baseDirectory, LogDirectory),
            CacheDirectory = ResolvePath(baseDirectory, CacheDirectory),
            TempDirectory = ResolvePath(baseDirectory, TempDirectory)
        };
    }

    private static string ResolvePath(string baseDirectory, string path)
    {
        return Path.IsPathFullyQualified(path)
            ? path
            : Path.GetFullPath(Path.Combine(baseDirectory, path));
    }
}
