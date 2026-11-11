using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable manifest/source reads and in-memory mutations only.
public sealed class AcceptanceManifestDeadLaneTests
{
    private static readonly string[] LiveReaderExemptions =
    [
        // TestCollectionGuardRules.FindLaneViolations needs the raw complement;
        // AcceptanceBalancedLanePoolSettings.Validate requires the pool-eligible lane.
        "Remainder",
        // ImpactReplayExecutionSource.IsDefaultFloor and ExperimentReplaySelection.IsFloor
        // use this lane's name and filter to preserve the process-spawning selection floor.
        "Process spawning parallel"
    ];

    [Fact]
    public void ShippedManifest_EveryDeclarationIsExecutedOrHasALiveReader()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        var classes = AcceptanceTestClassSourceScanner.Scan(root);
        Assert.NotEmpty(classes);
        var expanded = AcceptanceBalancedLaneExpander.Expand(settings.InfrastructureTestLanes, classes,
            new Dictionary<string, double>(), Assert.IsType<AcceptanceBalancedLanePoolSettings>(settings.BalancedLanePool));

        Assert.Empty(DeclarationErrors(settings.InfrastructureTestLanes, expanded.Lanes, LiveReaderExemptions));
    }

    [Fact]
    public void UnconstrainedLane_AddedToManifest_IsReportedByName()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        const string name = "Unused hand partition";
        AcceptanceTestLane[] lanes = [.. settings.InfrastructureTestLanes,
            new(name, "FullyQualifiedName~SyntheticUnusedTests")];
        var expanded = AcceptanceBalancedLaneExpander.Expand(lanes, AcceptanceTestClassSourceScanner.Scan(root),
            new Dictionary<string, double>(), Assert.IsType<AcceptanceBalancedLanePoolSettings>(settings.BalancedLanePool));

        Assert.Equal("Unused declaration: " + name,
            Assert.Single(DeclarationErrors(lanes, expanded.Lanes, LiveReaderExemptions)));
    }

    [Theory]
    [InlineData(false, "Undeclared exemption: live reader")]
    [InlineData(true, "Executed exemption: live reader")]
    public void Exemption_StaleOrExecuted_IsRejected(bool executed, string expected)
    {
        AcceptanceTestLane lane = new("live reader", "FullyQualifiedName~Example");
        Assert.Equal(expected, Assert.Single(DeclarationErrors(executed ? [lane] : [],
            executed ? [lane] : [], [lane.Name])));
    }

    private static string[] DeclarationErrors(IReadOnlyList<AcceptanceTestLane> declared,
        IReadOnlyList<AcceptanceTestLane> executed, IReadOnlyList<string> exemptions)
    {
        var declaredNames = declared.Select(lane => lane.Name).ToHashSet(StringComparer.Ordinal);
        var executedNames = executed.Select(lane => lane.Name).ToHashSet(StringComparer.Ordinal);
        return [.. declaredNames.Where(name => !executedNames.Contains(name) && !exemptions.Contains(name))
                .Select(name => "Unused declaration: " + name),
            .. exemptions.Where(name => !declaredNames.Contains(name)).Select(name => "Undeclared exemption: " + name),
            .. exemptions.Where(executedNames.Contains).Select(name => "Executed exemption: " + name)];
    }
}
