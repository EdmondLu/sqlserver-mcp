using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class ShowplanSummaryTests
{
    [Fact]
    public void SummarizeShowplanXml_ExtractsActionableSignals()
    {
        const string xml = """
                           <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
                             <BatchSequence>
                               <Batch>
                                 <Statements>
                                   <StmtSimple StatementText="SELECT * FROM dbo.Plan" StatementType="SELECT" StatementSubTreeCost="3.14" StatementEstRows="1000" StatementOptmLevel="FULL" StatementOptmEarlyAbortReason="TimeOut" CardinalityEstimationModelVersion="160">
                                     <QueryPlan>
                                       <MemoryGrantInfo SerialRequiredMemory="1024" SerialDesiredMemory="204800" RequiredMemory="2048" DesiredMemory="409600" RequestedMemory="409600" GrantedMemory="409600" MaxUsedMemory="2048" IsMemoryGrantFeedbackAdjusted="No" />
                                       <MissingIndexes>
                                         <MissingIndexGroup Impact="87.5">
                                           <MissingIndex Database="[TDSCM]" Schema="[dbo]" Table="[Plan]">
                                             <ColumnGroup Usage="EQUALITY">
                                               <Column Name="[PlanId]" ColumnId="1" />
                                             </ColumnGroup>
                                             <ColumnGroup Usage="INCLUDE">
                                               <Column Name="[Name]" ColumnId="2" />
                                             </ColumnGroup>
                                           </MissingIndex>
                                         </MissingIndexGroup>
                                       </MissingIndexes>
                                       <RelOp NodeId="0" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan" EstimatedTotalSubtreeCost="2.50" EstimateRows="1000">
                                         <IndexScan>
                                           <Object Database="[TDSCM]" Schema="[dbo]" Table="[Plan]" Index="[PK_Plan]" />
                                         </IndexScan>
                                       </RelOp>
                                       <RelOp NodeId="1" PhysicalOp="Sort" LogicalOp="Sort" EstimatedTotalSubtreeCost="0.20" EstimateRows="1000" />
                                       <RelOp NodeId="2" PhysicalOp="Hash Match" LogicalOp="Aggregate" EstimatedTotalSubtreeCost="0.30" />
                                       <RelOp NodeId="3" PhysicalOp="Key Lookup" LogicalOp="Key Lookup" EstimatedTotalSubtreeCost="0.10" />
                                       <RelOp NodeId="4" PhysicalOp="Parallelism" LogicalOp="Gather Streams" EstimatedTotalSubtreeCost="0.04" />
                                       <RelOp NodeId="5" PhysicalOp="Nested Loops" LogicalOp="Inner Join">
                                         <Warnings NoJoinPredicate="1">
                                           <PlanAffectingConvert ConvertIssue="Cardinality Estimate" Expression="CONVERT_IMPLICIT(int,[dbo].[Plan].[Code],0)" />
                                           <SpillToTempDb SpillLevel="1" />
                                         </Warnings>
                                       </RelOp>
                                     </QueryPlan>
                                   </StmtSimple>
                                 </Statements>
                               </Batch>
                             </BatchSequence>
                           </ShowPlanXML>
                           """;

        var summary = SqlMetadataService.SummarizeShowplanXml([xml]);

        Assert.Equal(1, summary.StatementCount);
        Assert.Equal(3.14m, summary.EstimatedTotalSubtreeCost);
        Assert.Equal(1, summary.OperatorCounts.ScanCount);
        Assert.Equal(1, summary.OperatorCounts.MissingIndexCount);
        Assert.Equal(1, summary.OperatorCounts.ImplicitConversionCount);
        Assert.Equal(1, summary.OperatorCounts.SortCount);
        Assert.Equal(1, summary.OperatorCounts.HashMatchCount);
        Assert.Equal(1, summary.OperatorCounts.KeyLookupCount);
        Assert.Equal(1, summary.OperatorCounts.ParallelismCount);
        Assert.Equal(1, summary.WarningCounts.SpillToTempDbCount);
        Assert.Equal(1, summary.WarningCounts.NoJoinPredicateCount);
        Assert.Equal(1, summary.WarningCounts.PlanAffectingConvertCount);
        Assert.Contains(summary.Risks, risk => risk.Code == "missing_index" && risk.Severity == "high");
        Assert.Contains(summary.Risks, risk => risk.Code == "implicit_conversion" && risk.Severity == "high");
        Assert.Contains(summary.Risks, risk => risk.Code == "spill_to_tempdb" && risk.Severity == "high");
        Assert.Contains(summary.Risks, risk => risk.Code == "no_join_predicate" && risk.Severity == "high");
        Assert.Contains(summary.Risks, risk => risk.Code == "optimizer_early_abort");
        Assert.Contains(summary.Risks, risk => risk.Code == "large_memory_grant");

        var statement = Assert.Single(summary.Statements);
        Assert.Equal("SELECT", statement.StatementType);
        Assert.Equal("TimeOut", statement.OptimizationEarlyAbortReason);
        Assert.Equal("160", statement.CardinalityEstimationModelVersion);
        Assert.Equal(204800, summary.MemoryGrant.MaxSerialDesiredMemoryKb);
        Assert.Equal(409600, summary.MemoryGrant.MaxRequestedMemoryKb);
        Assert.Contains("No", summary.MemoryGrant.FeedbackAdjustments);

        var missingIndex = Assert.Single(summary.MissingIndexes);
        Assert.Equal("[Plan]", missingIndex.Table);
        Assert.Equal(87.5m, missingIndex.Impact);
        Assert.Contains("[PlanId]", missingIndex.EqualityColumns);
        Assert.Contains("[Name]", missingIndex.IncludeColumns);

        Assert.Equal("Clustered Index Scan", summary.ExpensiveOperators[0].PhysicalOp);
        var warning = Assert.Single(summary.Warnings);
        Assert.Equal(5, warning.NodeId);
        Assert.Contains(warning.Details, detail => detail.Contains("PlanAffectingConvert"));
        Assert.Contains(warning.Details, detail => detail.Contains("SpillToTempDb"));
    }

    [Fact]
    public void SummarizeShowplanXml_ReturnsParseErrorsWithoutThrowing()
    {
        var summary = SqlMetadataService.SummarizeShowplanXml(["<not xml"]);

        Assert.Single(summary.ParseErrors);
        Assert.Empty(summary.Risks);
    }

    [Fact]
    public void SummarizeShowplanXml_DemotesConstantSideSeekPreservingConversionToInfo()
    {
        const string xml = """
                           <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
                             <BatchSequence>
                               <Batch>
                                 <Statements>
                                   <StmtSimple StatementText="SELECT Id FROM dbo.Plan WHERE Id = 1" StatementType="SELECT" StatementSubTreeCost="0.01">
                                     <QueryPlan>
                                       <RelOp NodeId="0" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimatedTotalSubtreeCost="0.01" EstimateRows="1">
                                         <IndexScan>
                                           <Object Database="[TDSCM]" Schema="[dbo]" Table="[Plan]" Index="[IX_Plan_Id]" />
                                           <SeekPredicates>
                                             <SeekPredicateNew>
                                               <SeekKeys>
                                                 <Prefix ScanType="EQ">
                                                   <RangeExpressions>
                                                     <ScalarOperator ScalarString="CONVERT_IMPLICIT(bigint,(1),0)">
                                                       <Convert DataType="bigint" Style="0" Implicit="1">
                                                         <ScalarOperator>
                                                           <Const ConstValue="(1)" />
                                                         </ScalarOperator>
                                                       </Convert>
                                                     </ScalarOperator>
                                                   </RangeExpressions>
                                                 </Prefix>
                                               </SeekKeys>
                                             </SeekPredicateNew>
                                           </SeekPredicates>
                                         </IndexScan>
                                       </RelOp>
                                     </QueryPlan>
                                   </StmtSimple>
                                 </Statements>
                               </Batch>
                             </BatchSequence>
                           </ShowPlanXML>
                           """;

        var summary = SqlMetadataService.SummarizeShowplanXml([xml]);

        Assert.Equal(1, summary.OperatorCounts.ImplicitConversionCount);
        Assert.Equal(0, summary.OperatorCounts.ColumnSideImplicitConversionCount);
        Assert.Equal(1, summary.OperatorCounts.SeekPreservingImplicitConversionCount);
        var risk = Assert.Single(summary.Risks, risk => risk.Code == "implicit_conversion");
        Assert.Equal("info", risk.Severity);
        Assert.Contains("retaining index seek", risk.Message, StringComparison.OrdinalIgnoreCase);
    }
}
