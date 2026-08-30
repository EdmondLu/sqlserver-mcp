using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Sql;

internal static partial class ReportPayloadCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static ReportPayloadInspection Inspect(
        string reportStringBase64,
        int maxPayloadMb,
        string? targetElementName = null,
        string? targetAttributeName = null,
        string? targetAttributeValue = null)
    {
        if (string.IsNullOrWhiteSpace(reportStringBase64))
        {
            throw PayloadError("base64", "ReportString Base64 content is required.");
        }

        ValidateSelector(targetElementName, targetAttributeName, targetAttributeValue);
        var maxBytes = checked(maxPayloadMb * 1024L * 1024L);
        var maxBase64Characters = checked((maxBytes * 4 / 3) + 16_384);
        if (reportStringBase64.Length > maxBase64Characters)
        {
            throw new SqlMcpException(
                ErrorCodes.ResultTooLarge,
                "ReportString exceeded the configured LOB inspection limit.",
                $"base64Characters={reportStringBase64.Length}; maxPayloadMb={maxPayloadMb}",
                "Raise limits.maxLobMb only after reviewing memory and response-size impact.");
        }

        byte[] compressedBytes;
        try
        {
            compressedBytes = Convert.FromBase64String(reportStringBase64);
        }
        catch (FormatException ex)
        {
            throw PayloadError("base64", "ReportString is not valid Base64.", ex);
        }

        if (compressedBytes.LongLength > maxBytes)
        {
            throw new SqlMcpException(
                ErrorCodes.ResultTooLarge,
                "Decoded GZip content exceeded the configured LOB inspection limit.",
                $"compressedBytes={compressedBytes.LongLength}; maxPayloadMb={maxPayloadMb}");
        }

        var gzipHeaderValid = compressedBytes.Length >= 10
                              && compressedBytes[0] == 0x1f
                              && compressedBytes[1] == 0x8b
                              && compressedBytes[2] == 0x08;
        if (!gzipHeaderValid)
        {
            throw PayloadError("gzip_header", "Decoded ReportString does not have a valid GZip header.");
        }

        byte[] decompressedBytes;
        try
        {
            decompressedBytes = DecompressBounded(compressedBytes, maxBytes);
        }
        catch (InvalidDataException ex)
        {
            throw PayloadError("gzip_stream", "ReportString GZip data is corrupt or incomplete.", ex);
        }

        var hasUtf8Bom = decompressedBytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        var xmlBytes = hasUtf8Bom ? decompressedBytes.AsSpan(3) : decompressedBytes.AsSpan();
        string xmlText;
        try
        {
            xmlText = StrictUtf8.GetString(xmlBytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw PayloadError("utf8", "Decompressed ReportString is not valid UTF-8.", ex);
        }

        var declarationMatch = XmlDeclarationRegex().Match(xmlText);
        var xmlDeclaration = declarationMatch.Success ? declarationMatch.Value : null;
        var declarationEncoding = declarationMatch.Success
            ? XmlEncodingRegex().Match(declarationMatch.Value).Groups["encoding"].Value
            : null;
        if (string.IsNullOrWhiteSpace(declarationEncoding))
        {
            declarationEncoding = null;
        }

        XDocument document;
        try
        {
            using var stream = new MemoryStream(decompressedBytes, writable: false);
            using var reader = XmlReader.Create(
                stream,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreComments = false,
                    IgnoreWhitespace = false,
                    CloseInput = false
                });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw PayloadError("xml", "Decompressed ReportString is not a parseable XML document.", ex);
        }

        if (document.Root is null)
        {
            throw PayloadError("xml", "Decompressed ReportString XML has no root element.");
        }

        var matchingNodes = targetElementName is null
            ? null
            : document.Root
                .DescendantsAndSelf()
                .Where(element => element.Name.LocalName.Equals(targetElementName, StringComparison.Ordinal))
                .Where(element => targetAttributeName is null
                    || element.Attributes().Any(attribute =>
                        attribute.Name.LocalName.Equals(targetAttributeName, StringComparison.Ordinal)
                        && (targetAttributeValue is null || attribute.Value.Equals(targetAttributeValue, StringComparison.Ordinal))))
                .ToArray();
        var newline = InspectNewlines(xmlText);
        var normalizedBase64 = RemoveBase64Whitespace(reportStringBase64);
        var selector = targetElementName is null
            ? null
            : new ReportNodeSelector(targetElementName, targetAttributeName, targetAttributeValue);

        return new ReportPayloadInspection(
            ValidationPassed: matchingNodes is null || matchingNodes.Length == 1,
            Base64Canonical: string.Equals(Convert.ToBase64String(compressedBytes), reportStringBase64, StringComparison.Ordinal),
            Base64ContainsWhitespace: normalizedBase64.Length != reportStringBase64.Length,
            Base64WhitespaceCharacterCount: reportStringBase64.Length - normalizedBase64.Length,
            ReportStringLengthCharacters: reportStringBase64.Length,
            ReportStringSha256Utf8: ComputeSha256Hex(Encoding.UTF8.GetBytes(reportStringBase64)),
            ReportStringSha256Utf16Le: ComputeSha256Hex(Encoding.Unicode.GetBytes(reportStringBase64)),
            CompressedLengthBytes: compressedBytes.LongLength,
            CompressedSha256: ComputeSha256Hex(compressedBytes),
            GzipHeaderValid: gzipHeaderValid,
            GzipFlags: compressedBytes[3],
            DecompressedLengthBytes: decompressedBytes.LongLength,
            DecompressedSha256: ComputeSha256Hex(decompressedBytes),
            HasUtf8Bom: hasUtf8Bom,
            Utf8Valid: true,
            XmlParseable: true,
            XmlDeclarationPresent: xmlDeclaration is not null,
            XmlDeclaration: xmlDeclaration,
            XmlDeclarationEncoding: declarationEncoding,
            RootName: document.Root.Name.LocalName,
            RootNamespace: document.Root.Name.NamespaceName,
            ElementCount: document.Root.DescendantsAndSelf().Count(),
            NewlineStyle: newline.Style,
            CrLfCount: newline.CrLfCount,
            LfCount: newline.LfCount,
            CrCount: newline.CrCount,
            Selector: selector,
            TargetNodeCount: matchingNodes?.Length,
            TargetNodeUnique: matchingNodes is null ? null : matchingNodes.Length == 1,
            FastReportRenderingValidated: false,
            ValidationBoundary: "Validated Base64, GZip integrity, UTF-8 bytes, BOM/declaration/newline metadata, XML parsing, and optional node uniqueness without reserializing XML. FastReport loading, scripting, data binding, and rendering were not executed.",
            CompressedBytes: compressedBytes,
            DecompressedBytes: decompressedBytes,
            Document: document);
    }

    public static ReportPayloadComparison Compare(
        ReportPayloadInspection original,
        ReportPayloadInspection candidate)
    {
        var compressedDiff = CompareBytes(original.CompressedBytes, candidate.CompressedBytes);
        var decompressedDiff = CompareBytes(original.DecompressedBytes, candidate.DecompressedBytes);
        var bomPreserved = original.HasUtf8Bom == candidate.HasUtf8Bom;
        var declarationPreserved = string.Equals(original.XmlDeclaration, candidate.XmlDeclaration, StringComparison.Ordinal);
        var declarationEncodingPreserved = string.Equals(
            original.XmlDeclarationEncoding,
            candidate.XmlDeclarationEncoding,
            StringComparison.OrdinalIgnoreCase);
        var newlineStylePreserved = string.Equals(original.NewlineStyle, candidate.NewlineStyle, StringComparison.Ordinal);
        var crLfCountPreserved = original.CrLfCount == candidate.CrLfCount;
        var lfCountPreserved = original.LfCount == candidate.LfCount;
        var crCountPreserved = original.CrCount == candidate.CrCount;
        var newlineCountsPreserved = crLfCountPreserved && lfCountPreserved && crCountPreserved;
        var base64CanonicalPreserved = original.Base64Canonical == candidate.Base64Canonical;
        var base64WhitespacePolicyPreserved = original.Base64ContainsWhitespace == candidate.Base64ContainsWhitespace
                                              && original.Base64WhitespaceCharacterCount == candidate.Base64WhitespaceCharacterCount;
        var rootPreserved = string.Equals(original.RootName, candidate.RootName, StringComparison.Ordinal)
                            && string.Equals(original.RootNamespace, candidate.RootNamespace, StringComparison.Ordinal);
        var selectorPreserved = original.Selector is null
                                || (candidate.TargetNodeUnique == true
                                    && original.TargetNodeCount == candidate.TargetNodeCount);

        return new ReportPayloadComparison(
            CompressedExactMatch: compressedDiff.ExactMatch,
            DecompressedExactMatch: decompressedDiff.ExactMatch,
            CompressedDiff: compressedDiff,
            DecompressedDiff: decompressedDiff,
            BomPreserved: bomPreserved,
            XmlDeclarationPreserved: declarationPreserved,
            XmlDeclarationEncodingPreserved: declarationEncodingPreserved,
            NewlineStylePreserved: newlineStylePreserved,
            CrLfCountPreserved: crLfCountPreserved,
            LfCountPreserved: lfCountPreserved,
            CrCountPreserved: crCountPreserved,
            NewlineCountsPreserved: newlineCountsPreserved,
            Base64CanonicalPreserved: base64CanonicalPreserved,
            Base64WhitespacePolicyPreserved: base64WhitespacePolicyPreserved,
            RootPreserved: rootPreserved,
            TargetSelectorInvariantPreserved: selectorPreserved,
            ByteLevelInvariantsPreserved: bomPreserved
                                          && declarationPreserved
                                          && declarationEncodingPreserved
                                          && newlineStylePreserved
                                          && newlineCountsPreserved
                                          && base64CanonicalPreserved
                                          && base64WhitespacePolicyPreserved
                                          && rootPreserved
                                          && selectorPreserved,
            CandidateValidationPassed: candidate.ValidationPassed,
            FastReportRenderingValidated: false);
    }

    public static ReportExactReplacementProof ProveExactReplacement(
        ReportPayloadInspection original,
        ReportPayloadInspection candidate,
        string originalFragmentBase64,
        string replacementFragmentBase64)
    {
        var originalFragment = DecodeFragment(
            originalFragmentBase64,
            "original_fragment_base64",
            original.DecompressedLengthBytes);
        var replacementFragment = DecodeFragment(
            replacementFragmentBase64,
            "replacement_fragment_base64",
            candidate.DecompressedLengthBytes);
        if (originalFragment.Length == 0)
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "originalFragmentBase64 must decode to at least one byte.",
                null,
                "Provide the exact non-empty byte fragment from the original decompressed payload.");
        }

        var occurrenceOffsets = FindOccurrenceOffsets(original.DecompressedBytes, originalFragment);
        byte[]? expectedCandidate = null;
        if (occurrenceOffsets.Length == 1)
        {
            var offset = occurrenceOffsets[0];
            expectedCandidate = new byte[
                checked(original.DecompressedBytes.Length - originalFragment.Length + replacementFragment.Length)];
            original.DecompressedBytes.AsSpan(0, offset).CopyTo(expectedCandidate);
            replacementFragment.AsSpan().CopyTo(expectedCandidate.AsSpan(offset));
            original.DecompressedBytes.AsSpan(offset + originalFragment.Length)
                .CopyTo(expectedCandidate.AsSpan(offset + replacementFragment.Length));
        }

        var exactCandidateMatch = expectedCandidate is not null
                                  && expectedCandidate.AsSpan().SequenceEqual(candidate.DecompressedBytes);
        var failureReason = occurrenceOffsets.Length switch
        {
            0 => "original_fragment_not_found",
            > 1 => "original_fragment_not_unique",
            _ when !exactCandidateMatch => "candidate_is_not_exact_single_replacement",
            _ => null
        };

        return new ReportExactReplacementProof(
            Proved: exactCandidateMatch,
            FailureReason: failureReason,
            OriginalFragmentLengthBytes: originalFragment.LongLength,
            ReplacementFragmentLengthBytes: replacementFragment.LongLength,
            OriginalFragmentSha256: ComputeSha256Hex(originalFragment),
            ReplacementFragmentSha256: ComputeSha256Hex(replacementFragment),
            OriginalFragmentOccurrenceCount: occurrenceOffsets.Length,
            ReplacementCount: exactCandidateMatch ? 1 : 0,
            ReplacementOffsetBytes: occurrenceOffsets.Length == 1 ? occurrenceOffsets[0] : null,
            ExpectedCandidateLengthBytes: expectedCandidate?.LongLength,
            ExpectedCandidateSha256: expectedCandidate is null ? null : ComputeSha256Hex(expectedCandidate),
            ActualCandidateLengthBytes: candidate.DecompressedLengthBytes,
            ActualCandidateSha256: candidate.DecompressedSha256,
            ExactCandidateMatch: exactCandidateMatch);
    }

    public static bool IsSafeForGuardedPatch(
        ReportPayloadInspection candidate,
        ReportPayloadComparison comparison,
        ReportExactReplacementProof? exactReplacementProof)
    {
        return candidate.ValidationPassed
               && comparison.ByteLevelInvariantsPreserved
               && exactReplacementProof?.Proved == true;
    }

    public static ReportPayloadReplacementBuild BuildReplacementCandidate(
        string originalReportStringBase64,
        string originalFragmentBase64,
        string replacementFragmentBase64,
        int maxPayloadMb,
        string? targetElementName = null,
        string? targetAttributeName = null,
        string? targetAttributeValue = null)
    {
        var original = Inspect(
            originalReportStringBase64,
            maxPayloadMb,
            targetElementName,
            targetAttributeName,
            targetAttributeValue);
        var maxBytes = checked(maxPayloadMb * 1024L * 1024L);
        var originalFragment = DecodeFragment(
            originalFragmentBase64,
            "original_fragment_base64",
            original.DecompressedLengthBytes);
        if (originalFragment.Length == 0)
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "originalFragmentBase64 must decode to at least one byte.",
                null,
                "Provide the exact non-empty byte fragment from the original decompressed payload.");
        }

        var replacementFragment = DecodeFragment(
            replacementFragmentBase64,
            "replacement_fragment_base64",
            maxBytes);
        var occurrenceOffsets = FindOccurrenceOffsets(original.DecompressedBytes, originalFragment);
        if (occurrenceOffsets.Length != 1)
        {
            var reason = occurrenceOffsets.Length == 0
                ? "original_fragment_not_found"
                : "original_fragment_not_unique";
            throw new SqlMcpException(
                ErrorCodes.PayloadInvariantFailed,
                "Offline report candidate requires the original byte fragment to occur exactly once.",
                $"reason={reason}; occurrenceCount={occurrenceOffsets.Length}",
                "Use the exact Base64-encoded bytes of a unique fragment from the decompressed original payload.",
                errorDetails: new
                {
                    reason,
                    occurrenceCount = occurrenceOffsets.Length,
                    originalFragmentLengthBytes = originalFragment.LongLength,
                    originalFragmentSha256 = ComputeSha256Hex(originalFragment)
                });
        }

        var candidateLength = checked(
            original.DecompressedBytes.LongLength - originalFragment.LongLength + replacementFragment.LongLength);
        if (candidateLength > maxBytes)
        {
            throw new SqlMcpException(
                ErrorCodes.ResultTooLarge,
                "Offline report candidate exceeded the configured LOB limit.",
                $"candidateLengthBytes={candidateLength}; maxPayloadBytes={maxBytes}",
                "Use a smaller replacement or raise limits.maxLobMb only after reviewing memory impact.",
                errorDetails: new { candidateLengthBytes = candidateLength, maxPayloadBytes = maxBytes });
        }

        var replacementOffset = occurrenceOffsets[0];
        var candidateBytes = new byte[checked((int)candidateLength)];
        original.DecompressedBytes.AsSpan(0, replacementOffset).CopyTo(candidateBytes);
        replacementFragment.AsSpan().CopyTo(candidateBytes.AsSpan(replacementOffset));
        original.DecompressedBytes.AsSpan(replacementOffset + originalFragment.Length)
            .CopyTo(candidateBytes.AsSpan(replacementOffset + replacementFragment.Length));

        var candidateCompressedBytes = Compress(candidateBytes);
        var candidateReportStringBase64 = Convert.ToBase64String(candidateCompressedBytes);
        var candidate = Inspect(
            candidateReportStringBase64,
            maxPayloadMb,
            targetElementName,
            targetAttributeName,
            targetAttributeValue);
        var comparison = Compare(original, candidate);
        var exactReplacementProof = ProveExactReplacement(
            original,
            candidate,
            originalFragmentBase64,
            replacementFragmentBase64);
        var safeForGuardedPatch = IsSafeForGuardedPatch(candidate, comparison, exactReplacementProof);

        return new ReportPayloadReplacementBuild(
            CandidateReportStringBase64: candidateReportStringBase64,
            Original: original,
            Candidate: candidate,
            Comparison: comparison,
            ExactReplacementProof: exactReplacementProof,
            SafeForGuardedPatch: safeForGuardedPatch,
            OutputPolicy: new ReportPayloadReplacementOutputPolicy(
                OriginalBase64Canonical: original.Base64Canonical,
                OriginalBase64ContainsWhitespace: original.Base64ContainsWhitespace,
                CandidateBase64Canonical: candidate.Base64Canonical,
                CandidateBase64ContainsWhitespace: candidate.Base64ContainsWhitespace,
                Base64OutputPolicy: "canonical-rfc4648-no-whitespace",
                CompressionOutputPolicy: "new-dotnet-gzip-stream",
                DecompressedBytesOutsideReplacementPreserved: exactReplacementProof.Proved,
                XmlReserialized: false,
                CompressedStreamExactPreservationGuaranteed: false,
                GzipHeaderMetadataPreservationGuaranteed: false,
                PolicyNote: original.Base64Canonical
                    ? "The candidate uses canonical Base64 and a newly created GZip stream. Exact preservation applies only to decompressed bytes outside the one replacement, not to compressed bytes or GZip header metadata."
                    : "The original Base64 text is non-canonical or contains whitespace. The candidate is canonical Base64 with no whitespace, so Base64 text policy is intentionally not preserved and safeForGuardedPatch remains false. Compressed bytes and GZip header metadata are not preserved."),
            FastReportRenderingValidated: false,
            ValidationBoundary: "Validated exact decompressed-byte replacement, Base64/GZip/UTF-8/XML integrity, byte invariants, and optional selector uniqueness. FastReport loading, scripting, data binding, and rendering were not executed.");
    }

    private static byte[] DecompressBounded(byte[] compressedBytes, long maxBytes)
    {
        using var input = new MemoryStream(compressedBytes, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: false);
        using var output = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = gzip.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maxBytes)
            {
                throw new SqlMcpException(
                    ErrorCodes.ResultTooLarge,
                    "Decompressed ReportString exceeded the configured LOB inspection limit.",
                    $"maxPayloadBytes={maxBytes}");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static byte[] Compress(byte[] decompressedBytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(decompressedBytes, 0, decompressedBytes.Length);
        }

        return output.ToArray();
    }

    private static ByteDiffSummary CompareBytes(byte[] original, byte[] candidate)
    {
        var commonPrefix = 0;
        var minLength = Math.Min(original.Length, candidate.Length);
        while (commonPrefix < minLength && original[commonPrefix] == candidate[commonPrefix])
        {
            commonPrefix++;
        }

        var commonSuffix = 0;
        while (commonSuffix < minLength - commonPrefix
               && original[original.Length - commonSuffix - 1] == candidate[candidate.Length - commonSuffix - 1])
        {
            commonSuffix++;
        }

        var exactMatch = original.AsSpan().SequenceEqual(candidate);
        return new ByteDiffSummary(
            ExactMatch: exactMatch,
            OriginalLengthBytes: original.LongLength,
            CandidateLengthBytes: candidate.LongLength,
            LengthDeltaBytes: candidate.LongLength - original.LongLength,
            FirstDifferenceOffset: exactMatch ? null : commonPrefix,
            CommonPrefixLengthBytes: commonPrefix,
            CommonSuffixLengthBytes: commonSuffix,
            OriginalSha256: ComputeSha256Hex(original),
            CandidateSha256: ComputeSha256Hex(candidate));
    }

    private static NewlineInspection InspectNewlines(string text)
    {
        var crLf = 0;
        var lf = 0;
        var cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crLf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        var kinds = (crLf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        var style = kinds switch
        {
            0 => "none",
            > 1 => "mixed",
            _ when crLf > 0 => "crlf",
            _ when lf > 0 => "lf",
            _ => "cr"
        };
        return new NewlineInspection(style, crLf, lf, cr);
    }

    private static void ValidateSelector(string? element, string? attribute, string? value)
    {
        if (element is not null && !XmlNameRegex().IsMatch(element))
        {
            throw PayloadError("selector", "targetElementName must be a simple XML local name.");
        }

        if (attribute is not null && !XmlNameRegex().IsMatch(attribute))
        {
            throw PayloadError("selector", "targetAttributeName must be a simple XML local name.");
        }

        if (element is null && (attribute is not null || value is not null))
        {
            throw PayloadError("selector", "targetElementName is required when an attribute selector is supplied.");
        }

        if (attribute is null && value is not null)
        {
            throw PayloadError("selector", "targetAttributeName is required when targetAttributeValue is supplied.");
        }
    }

    private static string RemoveBase64Whitespace(string value)
    {
        return string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
    }

    private static byte[] DecodeFragment(string value, string stage, long maxDecodedBytes)
    {
        var maxBase64Characters = checked((maxDecodedBytes * 4 / 3) + 16);
        if (value.Length > maxBase64Characters)
        {
            throw new SqlMcpException(
                ErrorCodes.ResultTooLarge,
                "Exact replacement fragment exceeds the corresponding decompressed payload boundary.",
                $"stage={stage}; base64Characters={value.Length}; maxDecodedBytes={maxDecodedBytes}");
        }

        try
        {
            var decoded = Convert.FromBase64String(value);
            if (decoded.LongLength > maxDecodedBytes)
            {
                throw new SqlMcpException(
                    ErrorCodes.ResultTooLarge,
                    "Exact replacement fragment exceeds the corresponding decompressed payload boundary.",
                    $"stage={stage}; decodedBytes={decoded.LongLength}; maxDecodedBytes={maxDecodedBytes}");
            }

            return decoded;
        }
        catch (FormatException ex)
        {
            throw new SqlMcpException(
                ErrorCodes.PayloadInvalid,
                "Exact replacement fragment is not valid Base64.",
                $"stage={stage}",
                "Encode the exact original and replacement byte fragments as Base64.",
                ex,
                errorDetails: new { stage });
        }
    }

    private static int[] FindOccurrenceOffsets(byte[] value, byte[] fragment)
    {
        var offsets = new List<int>();
        var searchStart = 0;
        while (searchStart <= value.Length - fragment.Length)
        {
            var relative = value.AsSpan(searchStart).IndexOf(fragment);
            if (relative < 0)
            {
                break;
            }

            var absolute = searchStart + relative;
            offsets.Add(absolute);
            searchStart = absolute + 1;
        }

        return offsets.ToArray();
    }

    private static string ComputeSha256Hex(ReadOnlySpan<byte> value)
    {
        return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    }

    private static SqlMcpException PayloadError(string stage, string message, Exception? inner = null)
    {
        return new SqlMcpException(
            ErrorCodes.PayloadInvalid,
            message,
            $"stage={stage}",
            "Keep ReportString as Base64 of the original GZip bytes and validate the decompressed UTF-8 XML without serializing it through SQL Server XML.",
            inner,
            errorDetails: new { stage });
    }

    [GeneratedRegex(@"\A<\?xml\s+[^?]*\?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex XmlDeclarationRegex();

    [GeneratedRegex("\\bencoding\\s*=\\s*[\"'](?<encoding>[^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex XmlEncodingRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex XmlNameRegex();

    private sealed record NewlineInspection(string Style, int CrLfCount, int LfCount, int CrCount);
}

internal sealed record ReportNodeSelector(
    string ElementName,
    string? AttributeName,
    string? AttributeValue);

internal sealed record ReportPayloadInspection(
    bool ValidationPassed,
    bool Base64Canonical,
    bool Base64ContainsWhitespace,
    int Base64WhitespaceCharacterCount,
    int ReportStringLengthCharacters,
    string ReportStringSha256Utf8,
    string ReportStringSha256Utf16Le,
    long CompressedLengthBytes,
    string CompressedSha256,
    bool GzipHeaderValid,
    byte GzipFlags,
    long DecompressedLengthBytes,
    string DecompressedSha256,
    bool HasUtf8Bom,
    bool Utf8Valid,
    bool XmlParseable,
    bool XmlDeclarationPresent,
    string? XmlDeclaration,
    string? XmlDeclarationEncoding,
    string RootName,
    string RootNamespace,
    int ElementCount,
    string NewlineStyle,
    int CrLfCount,
    int LfCount,
    int CrCount,
    ReportNodeSelector? Selector,
    int? TargetNodeCount,
    bool? TargetNodeUnique,
    bool FastReportRenderingValidated,
    string ValidationBoundary,
    [property: JsonIgnore] byte[] CompressedBytes,
    [property: JsonIgnore] byte[] DecompressedBytes,
    [property: JsonIgnore] XDocument Document);

internal sealed record ByteDiffSummary(
    bool ExactMatch,
    long OriginalLengthBytes,
    long CandidateLengthBytes,
    long LengthDeltaBytes,
    int? FirstDifferenceOffset,
    int CommonPrefixLengthBytes,
    int CommonSuffixLengthBytes,
    string OriginalSha256,
    string CandidateSha256);

internal sealed record ReportPayloadComparison(
    bool CompressedExactMatch,
    bool DecompressedExactMatch,
    ByteDiffSummary CompressedDiff,
    ByteDiffSummary DecompressedDiff,
    bool BomPreserved,
    bool XmlDeclarationPreserved,
    bool XmlDeclarationEncodingPreserved,
    bool NewlineStylePreserved,
    bool CrLfCountPreserved,
    bool LfCountPreserved,
    bool CrCountPreserved,
    bool NewlineCountsPreserved,
    bool Base64CanonicalPreserved,
    bool Base64WhitespacePolicyPreserved,
    bool RootPreserved,
    bool TargetSelectorInvariantPreserved,
    bool ByteLevelInvariantsPreserved,
    bool CandidateValidationPassed,
    bool FastReportRenderingValidated);

internal sealed record ReportExactReplacementProof(
    bool Proved,
    string? FailureReason,
    long OriginalFragmentLengthBytes,
    long ReplacementFragmentLengthBytes,
    string OriginalFragmentSha256,
    string ReplacementFragmentSha256,
    int OriginalFragmentOccurrenceCount,
    int ReplacementCount,
    int? ReplacementOffsetBytes,
    long? ExpectedCandidateLengthBytes,
    string? ExpectedCandidateSha256,
    long ActualCandidateLengthBytes,
    string ActualCandidateSha256,
    bool ExactCandidateMatch);

internal sealed record ReportPayloadReplacementBuild(
    string CandidateReportStringBase64,
    ReportPayloadInspection Original,
    ReportPayloadInspection Candidate,
    ReportPayloadComparison Comparison,
    ReportExactReplacementProof ExactReplacementProof,
    bool SafeForGuardedPatch,
    ReportPayloadReplacementOutputPolicy OutputPolicy,
    bool FastReportRenderingValidated,
    string ValidationBoundary);

internal sealed record ReportPayloadReplacementOutputPolicy(
    bool OriginalBase64Canonical,
    bool OriginalBase64ContainsWhitespace,
    bool CandidateBase64Canonical,
    bool CandidateBase64ContainsWhitespace,
    string Base64OutputPolicy,
    string CompressionOutputPolicy,
    bool DecompressedBytesOutsideReplacementPreserved,
    bool XmlReserialized,
    bool CompressedStreamExactPreservationGuaranteed,
    bool GzipHeaderMetadataPreservationGuaranteed,
    string PolicyNote);
