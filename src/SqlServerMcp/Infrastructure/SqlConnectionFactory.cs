using Microsoft.Data.SqlClient;
using SqlServerMcp.Configuration;

namespace SqlServerMcp.Infrastructure;

public sealed class SqlConnectionFactory
{
    private readonly SqlServerMcpOptions _options;
    private readonly IWindowsCredentialReader _credentialReader;
    private readonly SemaphoreSlim _connectionStringLock = new(1, 1);
    private string? _cachedConnectionString;

    public SqlConnectionFactory(SqlServerMcpOptions options, IWindowsCredentialReader credentialReader)
    {
        _options = options;
        _credentialReader = credentialReader;
    }

    public int CredentialReadCount { get; private set; }

    public async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = await GetConnectionStringAsync(cancellationToken);
        var connection = new SqlConnection(connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (SqlException ex)
        {
            await connection.DisposeAsync();
            throw new SqlMcpException(
                ErrorCodes.SqlConnectionFailed,
                "Failed to connect to SQL Server.",
                ex.Message,
                "Check server/database config, network reachability, and Credential Manager target.",
                ex);
        }
    }

    public async Task<string> GetConnectionStringAsync(CancellationToken cancellationToken)
    {
        if (_cachedConnectionString is not null)
        {
            return _cachedConnectionString;
        }

        await _connectionStringLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedConnectionString is not null)
            {
                return _cachedConnectionString;
            }

            var credential = _credentialReader.ReadGenericCredential(_options.CredentialTarget);
            CredentialReadCount++;

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = _options.Server,
                InitialCatalog = _options.Database,
                UserID = credential.UserName,
                Password = credential.Password,
                ConnectTimeout = _options.Limits.ConnectTimeoutSeconds,
                Encrypt = _options.Connection.Encrypt,
                TrustServerCertificate = _options.Connection.TrustServerCertificate,
                Pooling = true,
                ApplicationName = "Codex SqlServerMcp"
            };

            if (string.Equals(_options.Connection.ApplicationIntent, "ReadOnly", StringComparison.OrdinalIgnoreCase))
            {
                builder.ApplicationIntent = ApplicationIntent.ReadOnly;
            }

            _cachedConnectionString = builder.ConnectionString;
            return _cachedConnectionString;
        }
        finally
        {
            _connectionStringLock.Release();
        }
    }

    public void Reload()
    {
        _cachedConnectionString = null;
        SqlConnection.ClearAllPools();
    }
}
