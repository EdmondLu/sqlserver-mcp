namespace SqlServerMcp.Configuration;

public static class CommandLineOptions
{
    public static string GetConfigPath(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "sqlserver_mcp.json");
    }
}
