# Contributing

Thank you for helping improve SQL Server MCP.

## Development

1. Install the .NET 8 SDK on Windows.
2. Create a branch from `main`.
3. Keep changes focused and add tests for behavior changes.
4. Run:

```powershell
dotnet test SqlServerMcp.sln --nologo
```

## Security and Test Data

- Never commit real passwords, connection strings, tokens, server addresses, database names, usernames, SQL module bodies, query results, or logs.
- Use generic values such as `localhost`, `SampleDb`, and `readonly_user` in tests and documentation.
- Keep the database principal read-only. Do not weaken the SQL guard to make a test pass.
- Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## Pull Requests

Describe the user impact, security implications, and validation performed. Changes to query validation should include both allowed and rejected SQL cases.
