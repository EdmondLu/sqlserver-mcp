using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlServerMcp.Configuration;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Sql;
using SqlServerMcp.Tools;

namespace SqlServerMcp.Tests;

public sealed class ReportPayloadCodecTests
{
    private const string OriginalXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Report>\r\n  <Header Name=\"PageHeader\" Height=\"20\">old</Header>\r\n  <Footer>stable</Footer>\r\n</Report>\r\n";
    private const string CandidateXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Report>\r\n  <Header Name=\"PageHeader\" Height=\"21\">new</Header>\r\n  <Footer>stable</Footer>\r\n</Report>\r\n";
    private const string OriginalHeader = "<Header Name=\"PageHeader\" Height=\"20\">old</Header>";
    private const string ReplacementHeader = "<Header Name=\"PageHeader\" Height=\"21\">new</Header>";

    [Fact]
    public void Inspect_PreservesBomDeclarationNewlineCountsAndExactBytes()
    {
        var raw = BuildUtf8XmlBytes(OriginalXml, includeBom: true);
        var reportString = CompressToBase64(raw);

        var inspection = Inspect(reportString);

        Assert.True(inspection.ValidationPassed);
        Assert.True(inspection.HasUtf8Bom);
        Assert.True(inspection.XmlDeclarationPresent);
        Assert.Equal("utf-8", inspection.XmlDeclarationEncoding);
        Assert.Equal("crlf", inspection.NewlineStyle);
        Assert.Equal(5, inspection.CrLfCount);
        Assert.Equal(0, inspection.LfCount);
        Assert.Equal(0, inspection.CrCount);
        Assert.Equal("Report", inspection.RootName);
        Assert.Equal(1, inspection.TargetNodeCount);
        Assert.True(inspection.TargetNodeUnique);
        Assert.Equal(raw, inspection.DecompressedBytes);
        Assert.True(inspection.Base64Canonical);
        Assert.Equal(0, inspection.Base64WhitespaceCharacterCount);
        Assert.False(inspection.FastReportRenderingValidated);
    }

    [Fact]
    public void Inspect_Base64GzipRoundTripIsByteExact()
    {
        var raw = BuildUtf8XmlBytes(OriginalXml, includeBom: true);
        var reportString = CompressToBase64(raw);

        var inspection = ReportPayloadCodec.Inspect(reportString, 5);

        Assert.Equal(reportString, Convert.ToBase64String(inspection.CompressedBytes));
        Assert.Equal(raw, inspection.DecompressedBytes);
        Assert.Equal(LobValueCodec.ComputeSha256Hex(raw), inspection.DecompressedSha256);
        Assert.True(inspection.GzipHeaderValid);
    }

    [Fact]
    public void Compare_ReportsExactNewlineAndBase64Properties()
    {
        var original = InspectPayload(OriginalXml, includeBom: true);
        var candidate = InspectPayload(CandidateXml, includeBom: true);

        var comparison = ReportPayloadCodec.Compare(original, candidate);

        Assert.False(comparison.DecompressedExactMatch);
        Assert.True(comparison.BomPreserved);
        Assert.True(comparison.XmlDeclarationPreserved);
        Assert.True(comparison.NewlineStylePreserved);
        Assert.True(comparison.CrLfCountPreserved);
        Assert.True(comparison.LfCountPreserved);
        Assert.True(comparison.CrCountPreserved);
        Assert.True(comparison.NewlineCountsPreserved);
        Assert.True(comparison.Base64CanonicalPreserved);
        Assert.True(comparison.Base64WhitespacePolicyPreserved);
        Assert.True(comparison.ByteLevelInvariantsPreserved);
    }

    [Fact]
    public void Compare_RejectsChangedNewlineCountEvenWhenStyleIsUnchanged()
    {
        var original = InspectPayload(OriginalXml, includeBom: true);
        var changedXml = CandidateXml.Replace("<Footer>", "\r\n  <Footer>", StringComparison.Ordinal);
        var candidate = InspectPayload(changedXml, includeBom: true);

        var comparison = ReportPayloadCodec.Compare(original, candidate);

        Assert.True(comparison.NewlineStylePreserved);
        Assert.False(comparison.CrLfCountPreserved);
        Assert.False(comparison.NewlineCountsPreserved);
        Assert.False(comparison.ByteLevelInvariantsPreserved);
    }

    [Fact]
    public void Compare_ReportsChangedBase64CanonicalAndWhitespacePolicy()
    {
        var canonical = CompressToBase64(BuildUtf8XmlBytes(OriginalXml, includeBom: true));
        var wrapped = canonical.Insert(canonical.Length / 2, "\r\n");
        var original = Inspect(wrapped);
        var candidate = Inspect(canonical);

        var comparison = ReportPayloadCodec.Compare(original, candidate);

        Assert.False(original.Base64Canonical);
        Assert.True(candidate.Base64Canonical);
        Assert.False(comparison.Base64CanonicalPreserved);
        Assert.False(comparison.Base64WhitespacePolicyPreserved);
        Assert.False(comparison.ByteLevelInvariantsPreserved);
    }

    [Fact]
    public void ExactReplacement_ProvesRealHeaderTagByteReplacementWithBomAndDeclarationPreserved()
    {
        var original = InspectPayload(OriginalXml, includeBom: true);
        var candidate = InspectPayload(CandidateXml, includeBom: true);
        var comparison = ReportPayloadCodec.Compare(original, candidate);

        var proof = Prove(original, candidate, OriginalHeader, ReplacementHeader);

        Assert.True(proof.Proved);
        Assert.Equal(1, proof.OriginalFragmentOccurrenceCount);
        Assert.Equal(1, proof.ReplacementCount);
        Assert.True(proof.ExactCandidateMatch);
        Assert.Equal(candidate.DecompressedSha256, proof.ExpectedCandidateSha256);
        Assert.True(comparison.BomPreserved);
        Assert.True(comparison.XmlDeclarationPreserved);
        Assert.True(comparison.NewlineCountsPreserved);
    }

    [Fact]
    public void ExactReplacement_RejectsCandidateWithUnrelatedNodeChange()
    {
        var original = InspectPayload(OriginalXml, includeBom: true);
        var changed = CandidateXml.Replace("<Footer>stable</Footer>", "<Footer>unrelated edit</Footer>", StringComparison.Ordinal);
        var candidate = InspectPayload(changed, includeBom: true);

        var proof = Prove(original, candidate, OriginalHeader, ReplacementHeader);

        Assert.False(proof.Proved);
        Assert.Equal("candidate_is_not_exact_single_replacement", proof.FailureReason);
        Assert.Equal(1, proof.OriginalFragmentOccurrenceCount);
        Assert.Equal(0, proof.ReplacementCount);
    }

    [Fact]
    public void BuildReplacementCandidate_AddsHeaderSortByOneExactByteReplacement()
    {
        const string originalFragment = "<Header Name=\"PageHeader\"";
        const string replacementFragment = "<Header Name=\"PageHeader\" Sort=\"None\"";
        var originalBytes = BuildUtf8XmlBytes(OriginalXml, includeBom: true);
        var originalValue = CompressToBase64(originalBytes);

        var build = ReportPayloadCodec.BuildReplacementCandidate(
            originalValue,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(originalFragment)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(replacementFragment)),
            5,
            "Header",
            "Name",
            "PageHeader");

        var expectedBytes = ReplaceOnce(
            originalBytes,
            Encoding.UTF8.GetBytes(originalFragment),
            Encoding.UTF8.GetBytes(replacementFragment));
        Assert.Equal(expectedBytes, build.Candidate.DecompressedBytes);
        Assert.Equal(expectedBytes, DecompressFromBase64(build.CandidateReportStringBase64));
        Assert.True(build.SafeForGuardedPatch);
        Assert.True(build.ExactReplacementProof.Proved);
        Assert.Equal(1, build.ExactReplacementProof.OriginalFragmentOccurrenceCount);
        Assert.Equal(1, build.ExactReplacementProof.ReplacementCount);
        Assert.Equal(build.Candidate.DecompressedSha256, build.ExactReplacementProof.ExpectedCandidateSha256);
        Assert.True(build.Original.HasUtf8Bom);
        Assert.True(build.Candidate.HasUtf8Bom);
        Assert.Equal(build.Original.XmlDeclaration, build.Candidate.XmlDeclaration);
        Assert.Equal(build.Original.CrLfCount, build.Candidate.CrLfCount);
        Assert.Equal(build.Original.LfCount, build.Candidate.LfCount);
        Assert.Equal(build.Original.CrCount, build.Candidate.CrCount);
        Assert.True(build.OutputPolicy.DecompressedBytesOutsideReplacementPreserved);
        Assert.False(build.OutputPolicy.XmlReserialized);
        Assert.False(build.OutputPolicy.CompressedStreamExactPreservationGuaranteed);
        Assert.False(build.OutputPolicy.GzipHeaderMetadataPreservationGuaranteed);
        Assert.True(build.OutputPolicy.CandidateBase64Canonical);
        Assert.False(build.FastReportRenderingValidated);

        var offset = build.ExactReplacementProof.ReplacementOffsetBytes!.Value;
        var originalFragmentBytes = Encoding.UTF8.GetBytes(originalFragment);
        var replacementFragmentBytes = Encoding.UTF8.GetBytes(replacementFragment);
        Assert.Equal(originalBytes.AsSpan(0, offset).ToArray(), expectedBytes.AsSpan(0, offset).ToArray());
        Assert.Equal(
            originalBytes.AsSpan(offset + originalFragmentBytes.Length).ToArray(),
            expectedBytes.AsSpan(offset + replacementFragmentBytes.Length).ToArray());
    }

    [Theory]
    [InlineData("missing", 0, "original_fragment_not_found")]
    [InlineData("Report", 2, "original_fragment_not_unique")]
    public void BuildReplacementCandidate_RejectsNonUniqueOriginalFragment(
        string fragment,
        int expectedOccurrences,
        string expectedReason)
    {
        var originalValue = CompressToBase64(BuildUtf8XmlBytes(OriginalXml, includeBom: true));

        var ex = Assert.Throws<SqlMcpException>(() => ReportPayloadCodec.BuildReplacementCandidate(
            originalValue,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(fragment)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes("replacement")),
            5));

        Assert.Equal(ErrorCodes.PayloadInvariantFailed, ex.ErrorCode);
        Assert.Contains($"occurrenceCount={expectedOccurrences}", ex.Detail, StringComparison.Ordinal);
        Assert.Contains(expectedReason, ex.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(originalValue, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildReplacementCandidate_AllowsEmptyReplacement()
    {
        const string removed = " Height=\"20\"";
        var originalValue = CompressToBase64(BuildUtf8XmlBytes(OriginalXml, includeBom: true));

        var build = ReportPayloadCodec.BuildReplacementCandidate(
            originalValue,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(removed)),
            string.Empty,
            5,
            "Header",
            "Name",
            "PageHeader");

        Assert.True(build.SafeForGuardedPatch);
        Assert.Equal(0, build.ExactReplacementProof.ReplacementFragmentLengthBytes);
        Assert.Contains(
            "<Header Name=\"PageHeader\">old</Header>",
            Encoding.UTF8.GetString(build.Candidate.DecompressedBytes),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildReplacementCandidate_CanonicalizesNonCanonicalBase64AndReportsPolicyDrift()
    {
        const string originalFragment = "Height=\"20\"";
        const string replacementFragment = "Height=\"21\"";
        var canonical = CompressToBase64(BuildUtf8XmlBytes(OriginalXml, includeBom: true));
        var wrapped = canonical.Insert(canonical.Length / 2, "\r\n");

        var build = ReportPayloadCodec.BuildReplacementCandidate(
            wrapped,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(originalFragment)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(replacementFragment)),
            5,
            "Header",
            "Name",
            "PageHeader");

        Assert.False(build.Original.Base64Canonical);
        Assert.True(build.Original.Base64ContainsWhitespace);
        Assert.True(build.Candidate.Base64Canonical);
        Assert.False(build.Candidate.Base64ContainsWhitespace);
        Assert.False(build.Comparison.Base64CanonicalPreserved);
        Assert.False(build.Comparison.Base64WhitespacePolicyPreserved);
        Assert.False(build.SafeForGuardedPatch);
        Assert.Contains("canonical", build.OutputPolicy.PolicyNote, StringComparison.OrdinalIgnoreCase);
        Assert.False(build.OutputPolicy.CompressedStreamExactPreservationGuaranteed);
    }

    [Fact]
    public void BuildReplacementCandidate_EnforcesMaxLobBytesForReplacement()
    {
        var originalValue = CompressToBase64(BuildUtf8XmlBytes(OriginalXml, includeBom: true));
        var oversizedReplacement = new byte[(1024 * 1024) + 1];

        var ex = Assert.Throws<SqlMcpException>(() => ReportPayloadCodec.BuildReplacementCandidate(
            originalValue,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(OriginalHeader)),
            Convert.ToBase64String(oversizedReplacement),
            1));

        Assert.Equal(ErrorCodes.ResultTooLarge, ex.ErrorCode);
        Assert.DoesNotContain(Convert.ToBase64String(oversizedReplacement.AsSpan(0, 48)), ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceReportPayloadFragment_ReturnsMergeReadyPatchArgumentsWithoutReadingCredentials()
    {
        const string originalFragment = "<Header Name=\"PageHeader\"";
        const string replacementFragment = "<Header Name=\"PageHeader\" Sort=\"None\"";
        var options = CreateOfflineOptions(maxResultMb: 5, maxLobMb: 5);
        var connectionFactory = new SqlConnectionFactory(options, new FailIfCredentialRead());
        var service = new SqlMetadataService(options, connectionFactory, new ReadonlySqlGuard(options));
        var originalValue = CompressToBase64(BuildUtf8XmlBytes(OriginalXml, includeBom: true));

        var result = service.ReplaceReportPayloadFragment(
            originalValue,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(originalFragment)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(replacementFragment)),
            "Header",
            "Name",
            "PageHeader",
            new GuardedReportPatchTarget(
                "dbo",
                "pbReportFormat",
                "ReportId",
                "42",
                "int",
                "ReportString",
                "nvarchar"));
        var json = JsonSerializer.SerializeToElement(result, JsonResponse.Options);

        Assert.True(json.GetProperty("safeForGuardedPatch").GetBoolean());
        Assert.False(json.GetProperty("toolConnectedToDatabase").GetBoolean());
        Assert.Equal("generate_guarded_report_patch", json.GetProperty("nextRequest").GetProperty("toolName").GetString());
        Assert.True(json.GetProperty("nextRequest").GetProperty("readyToCall").GetBoolean());
        Assert.Empty(json.GetProperty("nextRequest").GetProperty("requiredTargetArguments").EnumerateArray());
        Assert.Equal(
            "pbReportFormat",
            json.GetProperty("nextRequest").GetProperty("arguments").GetProperty("table").GetString());
        Assert.Equal(
            json.GetProperty("candidateReportStringBase64").GetString(),
            json.GetProperty("nextRequest").GetProperty("arguments").GetProperty("candidateReportStringBase64").GetString());
        Assert.Equal(0, connectionFactory.CredentialReadCount);
    }

    [Fact]
    public void ReplaceReportPayloadFragment_EnforcesMaxResultBytesWithoutReadingCredentials()
    {
        const string originalFragment = "<Header Name=\"PageHeader\"";
        const string replacementFragment = "<Header Name=\"PageHeader\" Sort=\"None\"";
        var random = new Random(42);
        var alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var content = new string(Enumerable.Range(0, 700_000)
            .Select(_ => alphabet[random.Next(alphabet.Length)])
            .ToArray());
        var xml = OriginalXml.Replace(
            "<Footer>stable</Footer>",
            $"<Footer>{content}</Footer>",
            StringComparison.Ordinal);
        var options = CreateOfflineOptions(maxResultMb: 1, maxLobMb: 2);
        var connectionFactory = new SqlConnectionFactory(options, new FailIfCredentialRead());
        var service = new SqlMetadataService(options, connectionFactory, new ReadonlySqlGuard(options));

        var ex = Assert.Throws<SqlMcpException>(() => service.ReplaceReportPayloadFragment(
            CompressToBase64(BuildUtf8XmlBytes(xml, includeBom: true)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(originalFragment)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(replacementFragment)),
            "Header",
            "Name",
            "PageHeader",
            patchTarget: null));

        Assert.Equal(ErrorCodes.ResultTooLarge, ex.ErrorCode);
        Assert.Contains("maxResultBytes=1048576", ex.Detail, StringComparison.Ordinal);
        Assert.Equal(0, connectionFactory.CredentialReadCount);
    }

    [Fact]
    public void SafeForGuardedPatch_RequiresSuccessfulExactReplacementProof()
    {
        var original = InspectPayload(OriginalXml, includeBom: true);
        var candidate = InspectPayload(CandidateXml, includeBom: true);
        var comparison = ReportPayloadCodec.Compare(original, candidate);
        var validProof = Prove(original, candidate, OriginalHeader, ReplacementHeader);
        var invalidCandidate = InspectPayload(
            CandidateXml.Replace("stable", "unrelated", StringComparison.Ordinal),
            includeBom: true);
        var invalidComparison = ReportPayloadCodec.Compare(original, invalidCandidate);
        var invalidProof = Prove(original, invalidCandidate, OriginalHeader, ReplacementHeader);

        Assert.False(ReportPayloadCodec.IsSafeForGuardedPatch(candidate, comparison, null));
        Assert.True(ReportPayloadCodec.IsSafeForGuardedPatch(candidate, comparison, validProof));
        Assert.False(ReportPayloadCodec.IsSafeForGuardedPatch(invalidCandidate, invalidComparison, invalidProof));
    }

    [Theory]
    [InlineData("missing", 0, "original_fragment_not_found")]
    [InlineData("Report", 2, "original_fragment_not_unique")]
    public void ExactReplacement_RejectsOriginalFragmentThatOccursZeroOrTwice(
        string originalFragment,
        int expectedOccurrences,
        string expectedReason)
    {
        var original = InspectPayload(OriginalXml, includeBom: true);
        var candidate = InspectPayload(CandidateXml, includeBom: true);

        var proof = Prove(original, candidate, originalFragment, "replacement");

        Assert.False(proof.Proved);
        Assert.Equal(expectedOccurrences, proof.OriginalFragmentOccurrenceCount);
        Assert.Equal(0, proof.ReplacementCount);
        Assert.Equal(expectedReason, proof.FailureReason);
    }

    [Fact]
    public void Generate_ApplyZeroBranchReturnsBeforeTransactionLocksAndUpdate()
    {
        var originalValue = CompressToBase64(BuildUtf8XmlBytes(OriginalXml, includeBom: true));
        var candidateValue = CompressToBase64(BuildUtf8XmlBytes(CandidateXml, includeBom: true));
        var original = Inspect(originalValue);
        var candidate = Inspect(candidateValue);
        var comparison = ReportPayloadCodec.Compare(original, candidate);
        var proof = Prove(original, candidate, OriginalHeader, ReplacementHeader);

        var patch = GuardedReportPatchGenerator.Generate(
            "dbo",
            "pbReportFormat",
            "ReportId",
            "42",
            "int",
            "ReportString",
            "nvarchar",
            originalValue,
            candidateValue,
            original,
            candidate,
            comparison,
            proof);

        Assert.False(patch.ExecutesDatabaseWrite);
        Assert.False(patch.ApplyZeroDatabaseWriteAttempted);
        Assert.Equal("read_only_preflight", patch.ApplyZeroMode);
        Assert.True(patch.ExactReplacementProved);
        Assert.Contains("DECLARE @Apply bit = 0", patch.Script, StringComparison.Ordinal);
        Assert.Contains("IF @Apply IS NULL OR @Apply = 0", patch.Script, StringComparison.Ordinal);
        Assert.Contains("READ_ONLY_PREFLIGHT", patch.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("dry-run", patch.Script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(originalValue, patch.Script, StringComparison.Ordinal);

        var parser = new TSql180Parser(initialQuotedIdentifiers: false);
        var fragment = parser.Parse(new StringReader(patch.Script), out var parseErrors);
        Assert.Empty(parseErrors);
        var visitor = new PatchControlFlowVisitor(patch.Script);
        fragment.Accept(visitor);

        var applyZeroIf = Assert.Single(visitor.ApplyZeroIfStatements);
        var applyZeroReturns = new ReturnVisitor();
        applyZeroIf.ThenStatement.Accept(applyZeroReturns);
        Assert.Single(applyZeroReturns.Returns);
        Assert.Empty(applyZeroReturns.Updates);
        Assert.All(visitor.Updates, update => Assert.True(update.StartOffset > applyZeroIf.StartOffset + applyZeroIf.FragmentLength));
        Assert.All(visitor.BeginTransactions, transaction => Assert.True(transaction.StartOffset > applyZeroIf.StartOffset + applyZeroIf.FragmentLength));
        Assert.True(patch.Script.IndexOf("WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal) > applyZeroIf.StartOffset + applyZeroIf.FragmentLength);
    }

    [Fact]
    public void Generate_RejectsUnrelatedNodeChangeDespiteSelectorUniqueness()
    {
        var originalValue = CompressToBase64(BuildUtf8XmlBytes(OriginalXml, includeBom: true));
        var changed = CandidateXml.Replace("<Footer>stable</Footer>", "<Footer>unrelated edit</Footer>", StringComparison.Ordinal);
        var candidateValue = CompressToBase64(BuildUtf8XmlBytes(changed, includeBom: true));
        var original = Inspect(originalValue);
        var candidate = Inspect(candidateValue);
        var comparison = ReportPayloadCodec.Compare(original, candidate);
        var proof = Prove(original, candidate, OriginalHeader, ReplacementHeader);

        var ex = Assert.Throws<SqlMcpException>(() => GuardedReportPatchGenerator.Generate(
            "dbo", "pbReportFormat", "ReportId", "42", "int", "ReportString", "nvarchar",
            originalValue, candidateValue, original, candidate, comparison, proof));

        Assert.True(candidate.TargetNodeUnique);
        Assert.Equal(ErrorCodes.PayloadInvariantFailed, ex.ErrorCode);
        Assert.Contains("exact_replacement", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_RejectsBomChangeEvenWhenWholePayloadReplacementIsExact()
    {
        var originalRaw = BuildUtf8XmlBytes(OriginalXml, includeBom: true);
        var candidateRaw = BuildUtf8XmlBytes(CandidateXml, includeBom: false);
        var originalValue = CompressToBase64(originalRaw);
        var candidateValue = CompressToBase64(candidateRaw);
        var original = Inspect(originalValue);
        var candidate = Inspect(candidateValue);
        var comparison = ReportPayloadCodec.Compare(original, candidate);
        var proof = ReportPayloadCodec.ProveExactReplacement(
            original,
            candidate,
            Convert.ToBase64String(originalRaw),
            Convert.ToBase64String(candidateRaw));

        var ex = Assert.Throws<SqlMcpException>(() => GuardedReportPatchGenerator.Generate(
            "dbo", "pbReportFormat", "ReportId", "42", "int", "ReportString", "nvarchar",
            originalValue, candidateValue, original, candidate, comparison, proof));

        Assert.True(proof.Proved);
        Assert.Equal(ErrorCodes.PayloadInvariantFailed, ex.ErrorCode);
        Assert.Contains("utf8_bom", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_RejectsNewlineCountChangeEvenWhenItIsTheExactReplacement()
    {
        const string originalFooter = "  <Footer>stable</Footer>";
        const string replacementFooter = "\r\n  <Footer>stable</Footer>";
        var changedXml = OriginalXml.Replace(originalFooter, replacementFooter, StringComparison.Ordinal);
        var originalValue = CompressToBase64(BuildUtf8XmlBytes(OriginalXml, includeBom: true));
        var candidateValue = CompressToBase64(BuildUtf8XmlBytes(changedXml, includeBom: true));
        var original = Inspect(originalValue);
        var candidate = Inspect(candidateValue);
        var comparison = ReportPayloadCodec.Compare(original, candidate);
        var proof = Prove(original, candidate, originalFooter, replacementFooter);

        var ex = Assert.Throws<SqlMcpException>(() => GuardedReportPatchGenerator.Generate(
            "dbo", "pbReportFormat", "ReportId", "42", "int", "ReportString", "nvarchar",
            originalValue, candidateValue, original, candidate, comparison, proof));

        Assert.True(proof.Proved);
        Assert.True(comparison.NewlineStylePreserved);
        Assert.False(comparison.NewlineCountsPreserved);
        Assert.Equal(ErrorCodes.PayloadInvariantFailed, ex.ErrorCode);
        Assert.Contains("newline_counts", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_DetectsDuplicateTargetNodesWithoutClaimingRenderValidation()
    {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Report><Header Name=\"PageHeader\"/><Header Name=\"PageHeader\"/></Report>";

        var inspection = InspectPayload(xml, includeBom: true);

        Assert.False(inspection.ValidationPassed);
        Assert.Equal(2, inspection.TargetNodeCount);
        Assert.False(inspection.TargetNodeUnique);
        Assert.False(inspection.FastReportRenderingValidated);
    }

    [Theory]
    [InlineData("not-base64", "base64")]
    [InlineData("AQIDBA==", "gzip_header")]
    public void Inspect_RejectsInvalidContainers(string value, string expectedStage)
    {
        var ex = Assert.Throws<SqlMcpException>(() => ReportPayloadCodec.Inspect(value, 5));

        Assert.Equal(ErrorCodes.PayloadInvalid, ex.ErrorCode);
        Assert.Contains(expectedStage, ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_RejectsInvalidUtf8()
    {
        var value = CompressToBase64([0xef, 0xbb, 0xbf, 0xff, 0xfe]);

        var ex = Assert.Throws<SqlMcpException>(() => ReportPayloadCodec.Inspect(value, 5));

        Assert.Equal(ErrorCodes.PayloadInvalid, ex.ErrorCode);
        Assert.Contains("utf8", ex.Detail, StringComparison.Ordinal);
    }

    private static ReportExactReplacementProof Prove(
        ReportPayloadInspection original,
        ReportPayloadInspection candidate,
        string originalFragment,
        string replacementFragment)
    {
        return ReportPayloadCodec.ProveExactReplacement(
            original,
            candidate,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(originalFragment)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(replacementFragment)));
    }

    private static ReportPayloadInspection Inspect(string reportString)
    {
        return ReportPayloadCodec.Inspect(reportString, 5, "Header", "Name", "PageHeader");
    }

    private static ReportPayloadInspection InspectPayload(string xml, bool includeBom)
    {
        return Inspect(CompressToBase64(BuildUtf8XmlBytes(xml, includeBom)));
    }

    private static byte[] BuildUtf8XmlBytes(string xml, bool includeBom)
    {
        var content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(xml);
        return includeBom
            ? new byte[] { 0xef, 0xbb, 0xbf }.Concat(content).ToArray()
            : content;
    }

    private static string CompressToBase64(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(raw, 0, raw.Length);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    private static byte[] DecompressFromBase64(string value)
    {
        using var input = new MemoryStream(Convert.FromBase64String(value), writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] ReplaceOnce(byte[] original, byte[] oldFragment, byte[] replacementFragment)
    {
        var offset = original.AsSpan().IndexOf(oldFragment);
        Assert.True(offset >= 0);
        Assert.Equal(-1, original.AsSpan(offset + 1).IndexOf(oldFragment));
        var candidate = new byte[original.Length - oldFragment.Length + replacementFragment.Length];
        original.AsSpan(0, offset).CopyTo(candidate);
        replacementFragment.CopyTo(candidate.AsSpan(offset));
        original.AsSpan(offset + oldFragment.Length).CopyTo(candidate.AsSpan(offset + replacementFragment.Length));
        return candidate;
    }

    private static SqlServerMcpOptions CreateOfflineOptions(int maxResultMb, int maxLobMb)
    {
        return new SqlServerMcpOptions
        {
            Server = "offline.invalid,1433",
            Database = "OfflineDb",
            CredentialTarget = "sqlserver-mcp/offline-test-never-read",
            Limits = new LimitOptions
            {
                MaxResultMb = maxResultMb,
                MaxLobMb = maxLobMb
            }
        };
    }

    private sealed class FailIfCredentialRead : IWindowsCredentialReader
    {
        public WindowsCredential ReadGenericCredential(string target)
        {
            throw new InvalidOperationException($"Credential reader must not be called: {target}");
        }
    }

    private sealed class PatchControlFlowVisitor(string script) : TSqlFragmentVisitor
    {
        public List<IfStatement> ApplyZeroIfStatements { get; } = [];

        public List<UpdateStatement> Updates { get; } = [];

        public List<BeginTransactionStatement> BeginTransactions { get; } = [];

        public override void ExplicitVisit(IfStatement node)
        {
            var predicateText = script.Substring(node.Predicate.StartOffset, node.Predicate.FragmentLength);
            if (predicateText.Contains("@Apply", StringComparison.OrdinalIgnoreCase)
                && predicateText.Contains("0", StringComparison.Ordinal))
            {
                ApplyZeroIfStatements.Add(node);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            Updates.Add(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BeginTransactionStatement node)
        {
            BeginTransactions.Add(node);
            base.ExplicitVisit(node);
        }
    }

    private sealed class ReturnVisitor : TSqlFragmentVisitor
    {
        public List<ReturnStatement> Returns { get; } = [];

        public List<UpdateStatement> Updates { get; } = [];

        public override void ExplicitVisit(ReturnStatement node)
        {
            Returns.Add(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            Updates.Add(node);
            base.ExplicitVisit(node);
        }
    }
}
