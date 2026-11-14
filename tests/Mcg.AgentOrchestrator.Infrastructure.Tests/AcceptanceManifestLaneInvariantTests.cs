using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: synthetic lanes and class descriptors; no shared state or I/O.
public sealed class AcceptanceManifestLaneInvariantTests
{
    [Fact]
    public void EmptyLane_SelectsNoScannedClass_ReportsLaneName()
    {
        AcceptanceTestLane[] lanes = [new("Empty lane", "FullyQualifiedName~MissingTests")];
        AcceptanceTestClassDescriptor[] classes = [new("Synthetic.PresentTests", null)];

        var violation = Assert.Single(AcceptanceManifestLaneInvariants.FindViolations(lanes, classes));

        Assert.Contains("Empty lane", violation);
    }

    [Fact]
    public void StaleXunitKey_NamesNoScannedCollection_ReportsLaneAndKey()
    {
        AcceptanceTestLane[] lanes =
        [
            new("Stale key lane", "FullyQualifiedName~PresentTests")
            {
                ExclusiveResourceKeys = ["xunit:Absent", "host:SyntheticHostOnly"]
            }
        ];
        AcceptanceTestClassDescriptor[] classes = [new("Synthetic.PresentTests", "Present")];

        var violation = Assert.Single(AcceptanceManifestLaneInvariants.FindViolations(lanes, classes));

        Assert.Contains("Stale key lane", violation);
        Assert.Contains("xunit:Absent", violation);
    }
}
