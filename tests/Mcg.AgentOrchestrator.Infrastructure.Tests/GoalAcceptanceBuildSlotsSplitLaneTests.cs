using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: reads checked-in configuration and mutates only in-memory lane copies.
public sealed class GoalAcceptanceBuildSlotsSplitLaneTests
{
    private const string OldName = "Goal acceptance build slots";
    private const string Prefix = "GoalAcceptanceVerifierDotnetBuildSlotTests";
    private const string TraitSuffix = "&Category!=HostIntegration";
    private const string OldFilter = "FullyQualifiedName~GoalAcceptanceVerifierDotnetBuildSlotTests|FullyQualifiedName~GateLanePlacementReceiptTests|FullyQualifiedName~FocusedEvidenceOverflowContinuationTests|FullyQualifiedName~StructuralCoverageVerifierPermitWaitTests|FullyQualifiedName~GoalAcceptanceVerifierDotnetBuildSlotTestsAnalyzerAuthority|FullyQualifiedName~RemoteLaneDeadlineTests|FullyQualifiedName~RemoteShardInvalidationStopTests";
    private static readonly string[] NewNames = [OldName + " A", OldName + " B", OldName + " C"];
    private static readonly AcceptanceTestLane OldLane = new(OldName, OldFilter, 335.4);
    private static readonly string[][] SuffixRosters =
    [
        ["SourceReverted", "SourceRevertedMutation", "SourceRevertedPolicyFile", "SourceRevertedDeclaredSet",
            "SourceRevertedCompileRed", "SourceRevertedPaths", "SourceRevertedMutationLineEndings"],
        ["IdenticalTreeReuse", "MainBaselineDiscoveryCache", "DefaultMainBaselineDiscoveryCache",
            "ShadowObservationPhase", "FocusedEvidence", "FindingBaselineAbsentSelections",
            "FocusedEvidenceProducerRouting", "FocusedUnionReceipts", "FocusedEvidenceBuildPhaseTimeout"],
        ["TestTamperGuard", "FindingBaselineRouting", "VerdictAndBuildCache", "RemoteTypedSlotClaims",
            "RemoteLaneExecution", "StructuralCoveragePermitHold", "PolicyShardScope", "AnalyzerAuthority",
            "LaneReceipt", "ConcurrentShardScheduling", "RemoteLaneResourceUsage", "RunnerInvocationAndTrx",
            "BaselineIntegrity", "SlotGateJobResources", "StructuralCoverageOverlap", "RemoteEarlyOffer",
            "LaneEarlyStop", "RemovedTestProject", "RemoteLanePolicy", "RemoteLaneMachineLocalKeys",
            "RemoteExecutorClaim", "RemoteExecutorClassClaim", "SharedApparatusInvalidation",
            "RemoteShadowPreemption", "LaneSlowdown", "ShardReceipts", "SshRemoteLaneDiagnostics",
            "GateHeartbeat", "RemoteOfferOrder", "NonPartitionOverlap", "RemoteLaneSerializedKeys",
            "MachineShardBudget", "LaneEarlyStopReceipt", "RemoteExecutorSlots", "TrustedBaselineDiscovery",
            "RemoteLaneOccupancy", "RemoteLaneTiming", "HeartbeatRunIdentity", "CohortRemoteLane",
            "LaneTestFailureRerun", "GitHeavyLaneKeys", "AdvisoryChecks", "SshRemoteLaneNoTrx", "SshRemoteLane",
            "TrustedBaselineCompletion", "RemoteSlotInstrumentation", "DeletedTestFileDiff", "RemoteLaneBudget",
            "RemoteLanePolicyConductEvent", "ShardPermitLaneClass", "HeartbeatRunClass", "CohortRerunCache",
            "AttributionLaneScope", "SshRemoteLaneFetchFailure", "AttributedSlotUser"]
    ];

    [Fact]
    public void LoadedManifest_ReplacesOldLaneWithThreeGoalWorktreeLanes()
    {
        var lanes = LoadLanes();
        Assert.DoesNotContain(lanes, lane => lane.Name == OldName);
        foreach (var name in NewNames) Assert.Single(lanes, lane => lane.Name == name);
        using var manifest = LoadManifest();
        var group = Assert.Single(manifest.RootElement.GetProperty("engine")
            .GetProperty("localTestPartitions").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "GoalWorktree");
        var groupNames = group.GetProperty("laneNames").EnumerateArray()
            .Select(item => item.GetString()!).ToArray();
        Assert.DoesNotContain(OldName, groupNames);
        Assert.Equal(NewNames, groupNames.Where(name => NewNames.Contains(name)).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldFilterClasses_MatchExactlyOneAssignedLane(bool resolveCollections)
    {
        var types = typeof(GoalAcceptanceBuildSlotsSplitLaneTests).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                type.GetMethods(BindingFlags.Instance | BindingFlags.Public).Any(method =>
                    method.GetCustomAttributes(inherit: true).Any(attribute => attribute is Xunit.FactAttribute)))
            .Select(type => new AcceptanceTestClassDescriptor(type.FullName!,
                type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name)).ToArray();
        AssertPartition(types, resolveCollections);
        AssertPartition(AcceptanceTestClassSourceScanner.Scan(InfrastructureTestSupport.FindRepositoryRoot()),
            resolveCollections);
    }

    [Fact]
    public void NewLanes_AreKeylessUnownedConstrained_VerifierKeepsKey()
    {
        var lanes = LoadLanes();
        using var manifest = LoadManifest();
        var rawLanes = manifest.RootElement.GetProperty("engine").GetProperty("infrastructureTestLanes");
        foreach (var name in NewNames)
        {
            var lane = Assert.Single(lanes, lane => lane.Name == name);
            Assert.Empty(lane.ExclusiveResourceKeys);
            Assert.Empty(lane.OwnedCollections);
            Assert.True(AcceptanceBalancedLaneExpander.IsConstrained(lane), name);
            var raw = Assert.Single(rawLanes.EnumerateArray(), item => item.GetProperty("name").GetString() == name);
            Assert.False(raw.TryGetProperty("exclusiveResourceKeys", out _));
            Assert.False(raw.TryGetProperty("ownedCollections", out _));
        }
        var verifier = Assert.Single(lanes, lane => lane.Name == "Goal acceptance verifier");
        Assert.Equal(new[] { "xunit:GoalAcceptanceVerifier", "xunit:JobAccounting" }, verifier.ExclusiveResourceKeys);
        Assert.Equal(new[] { "GoalAcceptanceVerifier" }, verifier.OwnedCollections);
    }

    [Theory]
    [InlineData("A", 673.3)]
    [InlineData("B", 587.3)]
    [InlineData("C", 570.6)]
    public void NewLaneEstimates_StayWithinSeededSums(string suffix, double sum)
    {
        var lane = Assert.Single(LoadLanes(), lane => lane.Name == OldName + " " + suffix);
        Assert.True(lane.EstimatedSerialSeconds > 0);
        Assert.True(lane.EstimatedSerialSeconds <= sum + 0.1);
    }

    [Fact]
    public void ExpandedManifest_EveryScannedClassHasExactlyOneLane()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        var classes = AcceptanceTestClassSourceScanner.Scan(root);
        Assert.NotEmpty(classes);
        Assert.NotNull(settings.BalancedLanePool);
        var expanded = AcceptanceBalancedLaneExpander.Expand(settings.InfrastructureTestLanes, classes,
            new Dictionary<string, double>(), settings.BalancedLanePool);
        foreach (var item in classes)
        {
            var matches = AcceptanceLaneMembership.LanesIncluding(expanded.Lanes, item.FullName);
            Assert.True(matches.Count == 1, item.FullName + ": " + string.Join(',', matches.Select(lane => lane.Name)));
            if (WasInOldLane(item.FullName)) Assert.Contains(Assert.Single(matches).Name, NewNames);
        }
    }

    [Fact]
    public void NegativeControls_DetectDissolutionDoubleAndMissingMembership()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        var lanes = settings.InfrastructureTestLanes;
        var source = Prefix + "SourceReverted";
        var focused = Prefix + "FocusedEvidence";
        Assert.Equal(NewNames[0], Assert.Single(AcceptanceLaneMembership.LanesIncluding(lanes, source)).Name);
        Assert.Equal(NewNames[1], Assert.Single(AcceptanceLaneMembership.LanesIncluding(lanes, focused)).Name);
        var dissolved = lanes.Select(lane => lane.Name == NewNames[0]
            ? lane with { Filter = lane.Filter.Replace(TraitSuffix, "", StringComparison.Ordinal) } : lane).ToArray();
        Assert.False(AcceptanceBalancedLaneExpander.IsConstrained(Assert.Single(dissolved,
            lane => lane.Name == NewNames[0])));
        var classes = AcceptanceTestClassSourceScanner.Scan(root);
        Assert.Contains(classes, item => item.FullName == source);
        Assert.NotNull(settings.BalancedLanePool);
        var expanded = AcceptanceBalancedLaneExpander.Expand(dissolved, classes,
            new Dictionary<string, double>(), settings.BalancedLanePool);
        Assert.StartsWith(settings.BalancedLanePool.LaneNamePrefix,
            Assert.Single(AcceptanceLaneMembership.LanesIncluding(expanded.Lanes, source)).Name);
        var doubled = lanes.Select(lane => lane.Name == NewNames[2]
            ? lane with { Filter = lane.Filter + "|FullyQualifiedName~" + focused } : lane).ToArray();
        Assert.Equal(new[] { NewNames[1], NewNames[2] },
            AcceptanceLaneMembership.LanesIncluding(doubled, focused).Select(lane => lane.Name));
        var missing = lanes.Select(lane => lane.Name == NewNames[0]
            ? lane with { Filter = lane.Filter.Replace("FullyQualifiedName~" + source + "|", "", StringComparison.Ordinal) }
            : lane).ToArray();
        Assert.Empty(AcceptanceLaneMembership.LanesIncluding(missing, source));
    }

    private static void AssertPartition(IReadOnlyList<AcceptanceTestClassDescriptor> classes, bool resolveCollections)
    {
        var lanes = LoadLanes();
        if (resolveCollections) lanes = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, classes);
        var oldClasses = classes.Where(item => WasInOldLane(item.FullName)).ToArray();
        Assert.NotEmpty(oldClasses);
        for (var index = 0; index < NewNames.Length; index++)
        {
            var expected = SuffixRosters[index].Select(suffix => Prefix + suffix).Concat(index switch
            {
                0 => new[] { "RemoteShardInvalidationStopTests", "FocusedEvidenceOverflowContinuationTests" },
                2 => new[] { "GateLanePlacementReceiptTests", "StructuralCoverageVerifierPermitWaitTests",
                    "StructuralCoverageVerifierPermitWaitTestsRetryAfterLanes", "RemoteLaneDeadlineTests" },
                _ => Array.Empty<string>()
            }).Order(StringComparer.Ordinal).ToArray();
            var selected = oldClasses.Where(item => AcceptanceLaneMembership.LanesIncluding(lanes, item.FullName)
                .Any(lane => lane.Name == NewNames[index])).ToArray();
            Assert.Equal(expected, selected.Select(item => RosterName(item.FullName))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        }
        foreach (var item in classes)
        {
            var matches = AcceptanceLaneMembership.LanesIncluding(lanes, item.FullName);
            if (WasInOldLane(item.FullName)) Assert.Contains(Assert.Single(matches).Name, NewNames);
            else Assert.DoesNotContain(matches, lane => NewNames.Contains(lane.Name));
        }
    }

    private static string RosterName(string fullName) => fullName.Split('+')
        .First(part => WasInOldLane(part)).Split('.').Last();

    private static bool WasInOldLane(string fullName) =>
        AcceptanceLaneMembership.LanesIncluding([OldLane], fullName).Count == 1;

    private static IReadOnlyList<AcceptanceTestLane> LoadLanes() =>
        AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;

    private static JsonDocument LoadManifest() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        InfrastructureTestSupport.FindRepositoryRoot(), "config", "acceptance-manifest.json")));
}
