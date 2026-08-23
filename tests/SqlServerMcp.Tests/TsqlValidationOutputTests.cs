using System.Text.Json;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class TsqlValidationOutputTests
{
    [Fact]
    public void EvaluateDeploymentReadiness_TargetDriftIsNeverReady()
    {
        var assessment = SqlMetadataService.EvaluateDeploymentReadiness(
            staticValidationPassed: true,
            targetComparisonState: "definition_mismatch");

        Assert.True(assessment.TargetCompared);
        Assert.True(assessment.TargetDriftDetected);
        Assert.False(assessment.DeploymentReady);
        Assert.Equal("high", assessment.RiskLevel);
        Assert.Contains("TARGET_LOGIC_MAY_BE_OVERWRITTEN", assessment.ReasonCodes);
    }

    [Fact]
    public void BuildReadyToDeployContract_LegacyValidationKeepsStaticSemanticsWhenTargetIsUncompared()
    {
        var assessment = SqlMetadataService.EvaluateDeploymentReadiness(
            staticValidationPassed: true,
            targetComparisonState: "not_compared");

        var contract = SqlMetadataService.BuildReadyToDeployContract(
            staticValidationPassed: true,
            deploymentReady: assessment.DeploymentReady,
            preserveLegacyStaticSemantics: true);

        Assert.False(assessment.DeploymentReady);
        Assert.True(contract.ReadyToDeploy);
        Assert.Equal("legacy_alias_of_staticValidationPassed", contract.Semantics);
        Assert.True(contract.Deprecated);
    }

    [Fact]
    public void BuildReadyToDeployContract_NewValidateDeploymentUsesStrictSemanticsForTargetDrift()
    {
        var assessment = SqlMetadataService.EvaluateDeploymentReadiness(
            staticValidationPassed: true,
            targetComparisonState: "definition_mismatch");

        var contract = SqlMetadataService.BuildReadyToDeployContract(
            staticValidationPassed: true,
            deploymentReady: assessment.DeploymentReady,
            preserveLegacyStaticSemantics: false);

        Assert.False(assessment.DeploymentReady);
        Assert.False(contract.ReadyToDeploy);
        Assert.Equal("conservative_alias_of_deploymentReady", contract.Semantics);
        Assert.False(contract.Deprecated);
    }

    [Fact]
    public void BuildReadyToDeployContract_LegacyBatchKeepsStaticSemanticsDespiteTargetDrift()
    {
        var assessment = SqlMetadataService.EvaluateDeploymentReadiness(
            staticValidationPassed: true,
            targetComparisonState: "definition_mismatch");

        var contract = SqlMetadataService.BuildReadyToDeployContract(
            staticValidationPassed: true,
            deploymentReady: assessment.DeploymentReady,
            preserveLegacyStaticSemantics: true);

        Assert.False(assessment.DeploymentReady);
        Assert.True(contract.ReadyToDeploy);
        Assert.Equal("legacy_alias_of_staticValidationPassed", contract.Semantics);
        Assert.True(contract.Deprecated);
    }

    [Fact]
    public void BuildInconclusiveDeploymentResult_ViewDefinitionFailurePreservesStaticValidation()
    {
        var validation = new Dictionary<string, object?>
        {
            ["staticValidationPassed"] = true,
            ["readyToDeploy"] = true,
            ["readyToDeploySemantics"] = "legacy_alias_of_staticValidationPassed",
            ["readyToDeployDeprecated"] = true
        };
        var failure = SqlMetadataService.ClassifyTargetComparisonFailure(
            new SqlMcpException(
                ErrorCodes.ViewDefinitionPermissionRequired,
                "Definition is not available.",
                "OBJECT_DEFINITION returned NULL.",
                "Ask an administrator to review VIEW DEFINITION."));

        var result = SqlMetadataService.BuildInconclusiveDeploymentResult(
            validation,
            new { schema = "dbo", name = "Sample" },
            staticValidationPassed: true,
            Assert.IsType<SqlMetadataService.TargetComparisonFailure>(failure));
        var json = JsonSerializer.SerializeToElement(result, JsonResponse.Options);

        Assert.True(json.GetProperty("validation").GetProperty("readyToDeploy").GetBoolean());
        Assert.True(json.GetProperty("staticValidationPassed").GetBoolean());
        Assert.Equal("inconclusive", json.GetProperty("targetComparison").GetProperty("state").GetString());
        Assert.False(json.GetProperty("targetComparison").GetProperty("compared").GetBoolean());
        Assert.Equal(ErrorCodes.ViewDefinitionPermissionRequired, json.GetProperty("targetComparison").GetProperty("errorCode").GetString());
        Assert.Equal("VIEW DEFINITION", json.GetProperty("targetComparison").GetProperty("requiredPermission").GetString());
        Assert.False(json.GetProperty("deploymentReady").GetBoolean());
        Assert.False(json.GetProperty("readyToDeploy").GetBoolean());
        Assert.False(json.GetProperty("readyToDeployDeprecated").GetBoolean());
        Assert.Contains(
            json.GetProperty("deploymentAssessment").GetProperty("reasonCodes").EnumerateArray(),
            reason => reason.GetString() == "TARGET_COMPARISON_INCONCLUSIVE");
    }

    [Fact]
    public void BuildModuleDefinitionUnavailableException_MissingViewDefinitionReportsDatabasePermission()
    {
        var exception = SqlMetadataService.BuildModuleDefinitionUnavailableException(
            "dbo",
            "RestrictedModule",
            hasViewDefinition: false,
            isEncrypted: null);

        Assert.Equal(ErrorCodes.ViewDefinitionPermissionRequired, exception.ErrorCode);
        Assert.Contains("HAS_PERMS_BY_NAME", exception.Detail, StringComparison.Ordinal);
        Assert.Contains("database user", exception.Hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VIEW DEFINITION", exception.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildModuleDefinitionUnavailableException_AuthorizedButUnavailableDoesNotClaimPermissionFailure(bool isEncrypted)
    {
        var exception = SqlMetadataService.BuildModuleDefinitionUnavailableException(
            "dbo",
            "UnavailableModule",
            hasViewDefinition: true,
            isEncrypted: isEncrypted);

        Assert.Equal(ErrorCodes.ModuleDefinitionNotAvailable, exception.ErrorCode);
        Assert.Contains(isEncrypted ? "encrypted" : "not encrypted", exception.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("controlled source", exception.Hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deployment artifact", exception.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildModuleDefinitionUnavailableException_UnknownPermissionIsConservative()
    {
        var exception = SqlMetadataService.BuildModuleDefinitionUnavailableException(
            "dbo",
            "UnknownModule",
            hasViewDefinition: null,
            isEncrypted: null);

        Assert.Equal(ErrorCodes.ModuleDefinitionNotAvailable, exception.ErrorCode);
        Assert.Contains("could not be determined", exception.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("encrypted", exception.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildInconclusiveDeploymentResult_ModuleDefinitionUnavailableOmitsRequiredPermission()
    {
        var validation = new Dictionary<string, object?>
        {
            ["staticValidationPassed"] = true,
            ["readyToDeploy"] = true
        };
        var exception = SqlMetadataService.BuildModuleDefinitionUnavailableException(
            "dbo",
            "EncryptedModule",
            hasViewDefinition: true,
            isEncrypted: true);
        var failure = Assert.IsType<SqlMetadataService.TargetComparisonFailure>(
            SqlMetadataService.ClassifyTargetComparisonFailure(exception));

        Assert.Null(failure.RequiredPermission);

        var result = SqlMetadataService.BuildInconclusiveDeploymentResult(
            validation,
            new { schema = "dbo", name = "EncryptedModule" },
            staticValidationPassed: true,
            failure);
        var json = JsonSerializer.SerializeToElement(result, JsonResponse.Options);
        var targetComparison = json.GetProperty("targetComparison");

        Assert.Equal("inconclusive", targetComparison.GetProperty("state").GetString());
        Assert.Equal(ErrorCodes.ModuleDefinitionNotAvailable, targetComparison.GetProperty("errorCode").GetString());
        Assert.Equal(JsonValueKind.Null, targetComparison.GetProperty("requiredPermission").ValueKind);
        Assert.False(json.GetProperty("deploymentReady").GetBoolean());
        Assert.False(json.GetProperty("readyToDeploy").GetBoolean());
        Assert.False(json.GetProperty("readyToDeployDeprecated").GetBoolean());
    }

    [Theory]
    [InlineData(ErrorCodes.SqlConnectionFailed)]
    [InlineData(ErrorCodes.SqlTimeout)]
    [InlineData(ErrorCodes.UnknownError)]
    public void ClassifyTargetComparisonFailure_DoesNotSwallowConnectivityTimeoutOrUnknownErrors(string errorCode)
    {
        var failure = SqlMetadataService.ClassifyTargetComparisonFailure(
            new SqlMcpException(errorCode, "Failure must propagate."));

        Assert.Null(failure);
    }

    [Fact]
    public void EvaluateDeploymentReadiness_UncomparedTargetIsNeverReady()
    {
        var assessment = SqlMetadataService.EvaluateDeploymentReadiness(
            staticValidationPassed: true,
            targetComparisonState: "not_compared");

        Assert.False(assessment.TargetCompared);
        Assert.False(assessment.DeploymentReady);
        Assert.Contains("TARGET_NOT_COMPARED", assessment.ReasonCodes);
    }

    [Fact]
    public void FinalizeTsqlValidationResult_SummaryIsMateriallySmallerThanFull()
    {
        var references = Enumerable.Range(1, 250)
            .Select(index => new
            {
                schema = "dbo",
                name = $"ResolvedObject{index}",
                status = "resolved",
                columns = Enumerable.Range(1, 20).Select(column => $"Column{column}").ToArray()
            })
            .ToArray();
        var source = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ok"] = true,
            ["syntaxValid"] = true,
            ["staticValidationPassed"] = true,
            ["deploymentReady"] = false,
            ["readyToDeploy"] = false,
            ["diagnostics"] = Array.Empty<object>(),
            ["referenceSummary"] = new { objectCount = references.Length },
            ["temporaryObjectSummary"] = new { tempTableCount = 50 },
            ["target"] = new Dictionary<string, object?>
            {
                ["schema"] = "dbo",
                ["name"] = "Sample",
                ["exists"] = true,
                ["parameterCount"] = 100,
                ["parameters"] = Enumerable.Range(1, 100).Select(index => new { name = $"@p{index}" }).ToArray()
            },
            ["references"] = references,
            ["tempTables"] = Enumerable.Range(1, 50).Select(index => new { name = $"#Temp{index}" }).ToArray(),
            ["externalTempTables"] = Array.Empty<object>(),
            ["tableVariables"] = Array.Empty<object>()
        };

        var summary = SqlMetadataService.FinalizeTsqlValidationResult(source, "summary");
        var full = SqlMetadataService.FinalizeTsqlValidationResult(source, "full");
        var summaryLength = JsonSerializer.Serialize(summary, JsonResponse.Options).Length;
        var fullLength = JsonSerializer.Serialize(full, JsonResponse.Options).Length;

        Assert.False(summary.ContainsKey("references"));
        Assert.False(summary.ContainsKey("tempTables"));
        Assert.True(full.ContainsKey("references"));
        Assert.True(full.ContainsKey("tempTables"));
        Assert.True(summaryLength < fullLength / 5);
    }
}
