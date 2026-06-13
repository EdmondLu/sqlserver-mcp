# Security Policy

## Supported Versions

The latest `1.x` release receives security fixes.

## Reporting a Vulnerability

Please do not open a public issue for a suspected vulnerability. Use GitHub's private vulnerability reporting feature from the repository Security tab and include:

- affected version
- configuration needed to reproduce
- proof-of-concept SQL or MCP request with all real identifiers removed
- expected and actual behavior
- potential impact

Do not include live credentials, private database contents, internal hostnames, or production logs.

## Security Model

This project combines a syntax guard with SQL Server permissions. The guard reduces accidental or model-generated unsafe SQL, but the database login remains the final security boundary. Deploy with a dedicated least-privilege login that cannot write, administer the server, access other databases, or use linked servers.
