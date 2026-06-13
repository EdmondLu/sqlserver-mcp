using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Serilog;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Sql;
using SqlServerMcp.Tools;

var configPath = CommandLineOptions.GetConfigPath(args);
SqlServerMcpOptions options;

try
{
    options = SqlServerMcpOptions.Load(configPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"CONFIG_INVALID: {ex.Message}");
    return 2;
}

Directory.CreateDirectory(options.Runtime.LogDirectory);
Directory.CreateDirectory(options.Runtime.CacheDirectory);
Directory.CreateDirectory(options.Runtime.TempDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.File(
        Path.Combine(options.Runtime.LogDirectory, "sqlserver_mcp-.log"),
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: options.Logging.MaxFileSizeMb * 1024L * 1024L,
        retainedFileCountLimit: options.Logging.MaxRetainedFiles,
        rollOnFileSizeLimit: true,
        shared: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // stdout is reserved for MCP stdio protocol. All app logs go to files.
    builder.Logging.ClearProviders();
    builder.Services.AddSerilog(Log.Logger, dispose: true);

    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<IWindowsCredentialReader, WindowsCredentialReader>();
    builder.Services.AddSingleton<SqlConnectionFactory>();
    builder.Services.AddSingleton<ReadonlySqlGuard>();
    builder.Services.AddSingleton<SqlMetadataService>();
    builder.Services.AddSingleton<SqlServerToolService>();

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "SQL Server MCP terminated unexpectedly");
    Console.Error.WriteLine($"UNKNOWN_ERROR: {ex.Message}");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
