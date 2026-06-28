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
                                   <StmtSimple StatementText="SELECT * FROM dbo.Plan" StatementSubTreeCost="3.14">
                                     <QueryPlan>
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
        Assert.Contains(summary.Risks, risk => risk.Code == "missing_index" && risk.Severity == "high");
        Assert.Contains(summary.Risks, risk => risk.Code == "implicit_conversion" && risk.Severity == "high");

        var missingIndex = Assert.Single(summary.MissingIndexes);
        Assert.Equal("[Plan]", missingIndex.Table);
        Assert.Equal(87.5m, missingIndex.Impact);
        Assert.Contains("[PlanId]", missingIndex.EqualityColumns);
        Assert.Contains("[Name]", missingIndex.IncludeColumns);

        Assert.Equal("Clustered Index Scan", summary.ExpensiveOperators[0].PhysicalOp);
        var warning = Assert.Single(summary.Warnings);
        Assert.Equal(5, warning.NodeId);
        Assert.Contains(warning.Details, detail => detail.Contains("PlanAffectingConvert"));
    }

    [Fact]
    public void SummarizeShowplanXml_ReturnsParseErrorsWithoutThrowing()
    {
        var summary = SqlMetadataService.SummarizeShowplanXml(["<not xml"]);

        Assert.Single(summary.ParseErrors);
        Assert.Empty(summary.Risks);
    }
}
