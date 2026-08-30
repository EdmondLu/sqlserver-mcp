using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Sql;

internal static partial class GuardedReportPatchGenerator
{
    private static readonly HashSet<string> SupportedKeyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvarchar",
        "varchar",
        "int",
        "bigint",
        "uniqueidentifier"
    };

    public static GuardedReportPatch Generate(
        string schema,
        string table,
        string keyColumn,
        string keyValue,
        string keySqlType,
        string reportColumn,
        string reportColumnSqlType,
        string originalReportStringBase64,
        string candidateReportStringBase64,
        ReportPayloadInspection original,
        ReportPayloadInspection candidate,
        ReportPayloadComparison comparison,
        ReportExactReplacementProof exactReplacementProof)
    {
        ValidateIdentifier(schema, nameof(schema));
        ValidateIdentifier(table, nameof(table));
        ValidateIdentifier(keyColumn, nameof(keyColumn));
        ValidateIdentifier(reportColumn, nameof(reportColumn));
        keySqlType = NormalizeKeyType(keySqlType);
        reportColumnSqlType = NormalizeReportType(reportColumnSqlType);

        var failedInvariants = GetFailedInvariants(candidate, comparison, exactReplacementProof);
        if (failedInvariants.Length > 0)
        {
            throw new SqlMcpException(
                ErrorCodes.PayloadInvariantFailed,
                "Guarded report patch was not generated because exact replacement proof or required invariants failed.",
                string.Join(",", failedInvariants),
                "Inspect compare_report_payloads and rebuild the candidate by replacing the exact original byte fragment without XML reserialization.",
                errorDetails: new
                {
                    failedInvariants,
                    exactReplacementProof.OriginalFragmentOccurrenceCount,
                    exactReplacementProof.ReplacementCount,
                    exactReplacementProof.ExpectedCandidateSha256,
                    exactReplacementProof.FailureReason
                });
        }

        if (reportColumnSqlType == "varchar"
            && (originalReportStringBase64.Any(character => character > 0x7f)
                || candidateReportStringBase64.Any(character => character > 0x7f)))
        {
            throw new SqlMcpException(
                ErrorCodes.PayloadInvariantFailed,
                "varchar ReportString patch input must be ASCII-only.",
                null,
                "Use the exact Base64 text and preserve the target column type.");
        }

        var quotedTarget = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        var quotedKeyColumn = QuoteIdentifier(keyColumn);
        var quotedReportColumn = QuoteIdentifier(reportColumn);
        var reportTypeDeclaration = $"{reportColumnSqlType}(max)";
        var reportLiteralPrefix = reportColumnSqlType == "nvarchar" ? "N" : string.Empty;
        var originalValueHash = ComputeSqlValueHash(originalReportStringBase64, reportColumnSqlType);
        var candidateValueHash = ComputeSqlValueHash(candidateReportStringBase64, reportColumnSqlType);
        var keyDeclaration = BuildKeyDeclaration(keySqlType, keyValue);
        var rootName = EscapeSqlLiteral(candidate.RootName);
        var selectorAssertion = BuildSelectorAssertion(candidate.Selector);
        var bomAssertion = candidate.HasUtf8Bom
            ? "IF SUBSTRING(@CandidateRaw, 1, 3) <> 0xEFBBBF THROW 51017, 'Candidate UTF-8 BOM invariant failed.', 1;"
            : "IF SUBSTRING(@CandidateRaw, 1, 3) = 0xEFBBBF THROW 51017, 'Candidate unexpectedly contains a UTF-8 BOM.', 1;";

        var script = $$"""
            /*
              GENERATED ONLY: sqlserver-mcp does not execute this patch.
              @Apply=0 is a READ-ONLY PREFLIGHT. It exits before BEGIN TRANSACTION, lock hints, or any data modification.
              @Apply=1 is the only branch that opens a write transaction and applies the guarded change.
              Target: {{quotedTarget}}.{{quotedReportColumn}}
              Original ReportString SHA-256 ({{(reportColumnSqlType == "nvarchar" ? "UTF-16LE" : "ASCII")}}): {{originalValueHash}}
              Candidate ReportString SHA-256 ({{(reportColumnSqlType == "nvarchar" ? "UTF-16LE" : "ASCII")}}): {{candidateValueHash}}
              Original decompressed SHA-256: {{original.DecompressedSha256}}
              Candidate decompressed SHA-256: {{candidate.DecompressedSha256}}
              Exact raw-byte replacement proof: original occurrences={{exactReplacementProof.OriginalFragmentOccurrenceCount}}, replacements={{exactReplacementProof.ReplacementCount}}, offset={{exactReplacementProof.ReplacementOffsetBytes}}, expected candidate SHA-256={{exactReplacementProof.ExpectedCandidateSha256}}.
              Offline invariants: BOM={{comparison.BomPreserved}}, XML declaration={{comparison.XmlDeclarationPreserved}}, CRLF/LF/CR counts={{comparison.NewlineCountsPreserved}}, Base64 canonical/whitespace={{comparison.Base64CanonicalPreserved && comparison.Base64WhitespacePolicyPreserved}}, root={{comparison.RootPreserved}}, target selector={{comparison.TargetSelectorInvariantPreserved}}.
              FastReport runtime/rendering was NOT validated.
            */
            SET NOCOUNT ON;
            SET XACT_ABORT ON;

            DECLARE @Apply bit = 0; -- leave 0 for read-only preflight; set 1 only in an approved write session
            {{keyDeclaration}}
            DECLARE @ExpectedOriginal {{reportTypeDeclaration}} = {{reportLiteralPrefix}}'{{EscapeSqlLiteral(originalReportStringBase64)}}';
            DECLARE @Candidate {{reportTypeDeclaration}} = {{reportLiteralPrefix}}'{{EscapeSqlLiteral(candidateReportStringBase64)}}';
            DECLARE @ExpectedOriginalValueSha256 varbinary(32) = 0x{{originalValueHash}};
            DECLARE @ExpectedCandidateValueSha256 varbinary(32) = 0x{{candidateValueHash}};
            DECLARE @ExpectedOriginalCompressedSha256 varbinary(32) = 0x{{original.CompressedSha256}};
            DECLARE @ExpectedCandidateCompressedSha256 varbinary(32) = 0x{{candidate.CompressedSha256}};
            DECLARE @ExpectedOriginalRawSha256 varbinary(32) = 0x{{original.DecompressedSha256}};
            DECLARE @ExpectedCandidateRawSha256 varbinary(32) = 0x{{candidate.DecompressedSha256}};

            /* Read-only preflight: no transaction and no lock hints. */
            DECLARE @PreflightCurrent {{reportTypeDeclaration}};
            DECLARE @PreflightMatchedRows int;
            SELECT @PreflightCurrent = {{quotedReportColumn}}
            FROM {{quotedTarget}}
            WHERE {{quotedKeyColumn}} = @KeyValue;
            SET @PreflightMatchedRows = @@ROWCOUNT;

            IF @PreflightMatchedRows <> 1 THROW 51010, 'Expected exactly one target row during preflight.', 1;
            IF @PreflightCurrent IS NULL THROW 51011, 'Current ReportString is NULL during preflight.', 1;
            IF HASHBYTES('SHA2_256', CONVERT(varbinary(max), @PreflightCurrent)) <> @ExpectedOriginalValueSha256
                THROW 51012, 'Current ReportString hash no longer matches the audited original.', 1;
            IF @PreflightCurrent <> @ExpectedOriginal
                THROW 51013, 'Current ReportString differs despite the hash guard.', 1;

            DECLARE @OriginalCompressed varbinary(max) = CAST(N'' AS xml).value(
                'xs:base64Binary(sql:variable("@PreflightCurrent"))', 'varbinary(max)');
            DECLARE @CandidateCompressed varbinary(max) = CAST(N'' AS xml).value(
                'xs:base64Binary(sql:variable("@Candidate"))', 'varbinary(max)');
            IF HASHBYTES('SHA2_256', @OriginalCompressed) <> @ExpectedOriginalCompressedSha256
                THROW 51014, 'Original compressed-byte hash invariant failed.', 1;
            IF HASHBYTES('SHA2_256', @CandidateCompressed) <> @ExpectedCandidateCompressedSha256
                THROW 51015, 'Candidate compressed-byte hash invariant failed.', 1;

            DECLARE @OriginalRaw varbinary(max) = DECOMPRESS(@OriginalCompressed);
            DECLARE @CandidateRaw varbinary(max) = DECOMPRESS(@CandidateCompressed);
            IF HASHBYTES('SHA2_256', @OriginalRaw) <> @ExpectedOriginalRawSha256
                THROW 51016, 'Original decompressed-byte hash invariant failed.', 1;
            IF HASHBYTES('SHA2_256', @CandidateRaw) <> @ExpectedCandidateRawSha256
                THROW 51016, 'Candidate decompressed-byte hash invariant failed.', 1;
            {{bomAssertion}}

            DECLARE @CandidateXml xml = TRY_CONVERT(xml, @CandidateRaw);
            IF @CandidateXml IS NULL THROW 51018, 'Candidate bytes are not parseable XML.', 1;
            IF @CandidateXml.value('local-name((/*)[1])', 'sysname') <> N'{{rootName}}'
                THROW 51019, 'Candidate XML root invariant failed.', 1;
            {{selectorAssertion}}

            SELECT
                backup_kind = 'report_string_preimage_preview',
                target_object = N'{{EscapeSqlLiteral(quotedTarget)}}',
                key_value = CONVERT(nvarchar(4000), @KeyValue),
                report_string = @PreflightCurrent,
                report_string_sha256 = CONVERT(varchar(64), @ExpectedOriginalValueSha256, 2),
                decompressed_sha256 = CONVERT(varchar(64), @ExpectedOriginalRawSha256, 2),
                exact_replacement_count = 1;

            IF @Apply IS NULL OR @Apply = 0
            BEGIN
                SELECT
                    execution_mode = 'READ_ONLY_PREFLIGHT',
                    database_write_attempted = CONVERT(bit, 0),
                    checks_passed = CONVERT(bit, 1),
                    candidate_report_string_sha256 = CONVERT(varchar(64), @ExpectedCandidateValueSha256, 2),
                    candidate_decompressed_sha256 = CONVERT(varchar(64), @ExpectedCandidateRawSha256, 2),
                    fastreport_rendering_validated = CONVERT(bit, 0);
                RETURN;
            END;

            /* Apply branch only: reacquire the row under lock and repeat all mutable-source guards. */
            BEGIN TRANSACTION;
            BEGIN TRY
                DECLARE @LockedCurrent {{reportTypeDeclaration}};
                DECLARE @LockedMatchedRows int;
                SELECT @LockedCurrent = {{quotedReportColumn}}
                FROM {{quotedTarget}} WITH (UPDLOCK, HOLDLOCK)
                WHERE {{quotedKeyColumn}} = @KeyValue;
                SET @LockedMatchedRows = @@ROWCOUNT;

                IF @LockedMatchedRows <> 1 THROW 51030, 'Expected exactly one locked target row.', 1;
                IF @LockedCurrent IS NULL THROW 51031, 'Locked ReportString is NULL.', 1;
                IF HASHBYTES('SHA2_256', CONVERT(varbinary(max), @LockedCurrent)) <> @ExpectedOriginalValueSha256
                    THROW 51032, 'Locked ReportString hash no longer matches the audited original.', 1;
                IF @LockedCurrent <> @ExpectedOriginal
                    THROW 51033, 'Locked ReportString differs despite the hash guard.', 1;

                DECLARE @LockedOriginalCompressed varbinary(max) = CAST(N'' AS xml).value(
                    'xs:base64Binary(sql:variable("@LockedCurrent"))', 'varbinary(max)');
                IF HASHBYTES('SHA2_256', @LockedOriginalCompressed) <> @ExpectedOriginalCompressedSha256
                    THROW 51034, 'Locked original compressed-byte hash invariant failed.', 1;
                IF HASHBYTES('SHA2_256', DECOMPRESS(@LockedOriginalCompressed)) <> @ExpectedOriginalRawSha256
                    THROW 51035, 'Locked original decompressed-byte hash invariant failed.', 1;

                UPDATE {{quotedTarget}}
                SET {{quotedReportColumn}} = @Candidate
                WHERE {{quotedKeyColumn}} = @KeyValue
                  AND {{quotedReportColumn}} = @ExpectedOriginal
                  AND HASHBYTES('SHA2_256', CONVERT(varbinary(max), {{quotedReportColumn}})) = @ExpectedOriginalValueSha256;
                IF @@ROWCOUNT <> 1 THROW 51036, 'Guarded update did not affect exactly one row.', 1;

                DECLARE @Stored {{reportTypeDeclaration}};
                SELECT @Stored = {{quotedReportColumn}}
                FROM {{quotedTarget}}
                WHERE {{quotedKeyColumn}} = @KeyValue;
                IF HASHBYTES('SHA2_256', CONVERT(varbinary(max), @Stored)) <> @ExpectedCandidateValueSha256
                    THROW 51037, 'Stored ReportString hash differs from the candidate.', 1;
                IF @Stored <> @Candidate THROW 51038, 'Stored ReportString differs despite the candidate hash.', 1;

                COMMIT TRANSACTION;
                SELECT
                    execution_mode = 'APPLIED_AND_COMMITTED',
                    database_write_attempted = CONVERT(bit, 1),
                    applied = CONVERT(bit, 1),
                    candidate_report_string_sha256 = CONVERT(varchar(64), @ExpectedCandidateValueSha256, 2),
                    candidate_decompressed_sha256 = CONVERT(varchar(64), @ExpectedCandidateRawSha256, 2),
                    fastreport_rendering_validated = CONVERT(bit, 0);
            END TRY
            BEGIN CATCH
                IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH;
            """;

        return new GuardedReportPatch(
            Script: script,
            Target: $"{schema}.{table}.{reportColumn}",
            KeyColumn: keyColumn,
            KeySqlType: keySqlType,
            ReportColumnSqlType: reportColumnSqlType,
            ApplyDefault: false,
            ApplyZeroMode: "read_only_preflight",
            ApplyZeroDatabaseWriteAttempted: false,
            ExecutesDatabaseWrite: false,
            GeneratedSqlContainsGuardedUpdate: true,
            BackupMode: "embedded_preimage_and_read_only_preflight_result_set",
            OriginalValueSha256: originalValueHash,
            CandidateValueSha256: candidateValueHash,
            OriginalDecompressedSha256: original.DecompressedSha256,
            CandidateDecompressedSha256: candidate.DecompressedSha256,
            ByteLevelInvariantsPreserved: comparison.ByteLevelInvariantsPreserved,
            ExactReplacementProved: exactReplacementProof.Proved,
            ExpectedCandidateSha256: exactReplacementProof.ExpectedCandidateSha256!,
            FastReportRenderingValidated: false,
            RequiredSqlServerFeatures: ["HASHBYTES SHA2_256", "DECOMPRESS", "XML xs:base64Binary", "TRY_CONVERT(xml)"]);
    }

    private static string[] GetFailedInvariants(
        ReportPayloadInspection candidate,
        ReportPayloadComparison comparison,
        ReportExactReplacementProof proof)
    {
        var failures = new List<string>();
        if (!candidate.ValidationPassed) failures.Add("candidate_validation");
        if (!proof.Proved) failures.Add($"exact_replacement:{proof.FailureReason}");
        if (!comparison.BomPreserved) failures.Add("utf8_bom");
        if (!comparison.XmlDeclarationPreserved) failures.Add("xml_declaration");
        if (!comparison.XmlDeclarationEncodingPreserved) failures.Add("xml_declaration_encoding");
        if (!comparison.NewlineStylePreserved) failures.Add("newline_style");
        if (!comparison.NewlineCountsPreserved) failures.Add("newline_counts");
        if (!comparison.Base64CanonicalPreserved) failures.Add("base64_canonical_property");
        if (!comparison.Base64WhitespacePolicyPreserved) failures.Add("base64_whitespace_policy");
        if (!comparison.RootPreserved) failures.Add("xml_root");
        if (!comparison.TargetSelectorInvariantPreserved) failures.Add("target_selector");
        return failures.ToArray();
    }

    private static string BuildSelectorAssertion(ReportNodeSelector? selector)
    {
        if (selector is null)
        {
            return "-- No optional target-node selector was requested.";
        }

        var element = EscapeXQueryLiteral(selector.ElementName);
        var predicate = $"local-name(.)=\"{element}\"";
        if (selector.AttributeName is not null)
        {
            var attribute = EscapeXQueryLiteral(selector.AttributeName);
            var valuePredicate = selector.AttributeValue is null
                ? string.Empty
                : $" and .=\"{EscapeXQueryLiteral(selector.AttributeValue)}\"";
            predicate += $" and @*[local-name(.)=\"{attribute}\"{valuePredicate}]";
        }

        var xquery = EscapeSqlLiteral($"count(//*[{predicate}])");
        return $"IF @CandidateXml.value('{xquery}', 'int') <> 1 THROW 51019, 'Candidate target-node uniqueness invariant failed.', 1;";
    }

    private static string BuildKeyDeclaration(string keySqlType, string keyValue)
    {
        return keySqlType switch
        {
            "int" when int.TryParse(keyValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                => $"DECLARE @KeyValue int = {value.ToString(CultureInfo.InvariantCulture)};",
            "bigint" when long.TryParse(keyValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                => $"DECLARE @KeyValue bigint = {value.ToString(CultureInfo.InvariantCulture)};",
            "uniqueidentifier" when Guid.TryParse(keyValue, out var value)
                => $"DECLARE @KeyValue uniqueidentifier = '{value:D}';",
            "nvarchar" when keyValue.Length <= 4000
                => $"DECLARE @KeyValue nvarchar(4000) = N'{EscapeSqlLiteral(keyValue)}';",
            "varchar" when keyValue.Length <= 8000 && keyValue.All(character => character <= 0x7f)
                => $"DECLARE @KeyValue varchar(8000) = '{EscapeSqlLiteral(keyValue)}';",
            _ => throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "keyValue is invalid for keySqlType.",
                $"keySqlType={keySqlType}",
                "Use an exact int, bigint, uniqueidentifier, nvarchar, or ASCII varchar key value.")
        };
    }

    private static string NormalizeKeyType(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (!SupportedKeyTypes.Contains(normalized))
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "Unsupported keySqlType.",
                normalized,
                "Use nvarchar, varchar, int, bigint, or uniqueidentifier.");
        }

        return normalized;
    }

    private static string NormalizeReportType(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is not ("nvarchar" or "varchar"))
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "Unsupported reportColumnSqlType.",
                normalized,
                "Use nvarchar or varchar and confirm the real target column type first.");
        }

        return normalized;
    }

    private static string ComputeSqlValueHash(string value, string reportColumnSqlType)
    {
        var bytes = reportColumnSqlType == "nvarchar"
            ? Encoding.Unicode.GetBytes(value)
            : Encoding.ASCII.GetBytes(value);
        return LobValueCodec.ComputeSha256Hex(bytes);
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !SqlIdentifierRegex().IsMatch(value))
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                $"{parameterName} must be a simple SQL identifier.",
                null,
                "Do not include dots, brackets, quotes, whitespace, or SQL fragments.");
        }
    }

    private static string QuoteIdentifier(string value)
    {
        return $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    private static string EscapeSqlLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string EscapeXQueryLiteral(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{N}_@$#]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SqlIdentifierRegex();
}

internal sealed record GuardedReportPatch(
    string Script,
    string Target,
    string KeyColumn,
    string KeySqlType,
    string ReportColumnSqlType,
    bool ApplyDefault,
    string ApplyZeroMode,
    bool ApplyZeroDatabaseWriteAttempted,
    bool ExecutesDatabaseWrite,
    bool GeneratedSqlContainsGuardedUpdate,
    string BackupMode,
    string OriginalValueSha256,
    string CandidateValueSha256,
    string OriginalDecompressedSha256,
    string CandidateDecompressedSha256,
    bool ByteLevelInvariantsPreserved,
    bool ExactReplacementProved,
    string ExpectedCandidateSha256,
    bool FastReportRenderingValidated,
    string[] RequiredSqlServerFeatures);
