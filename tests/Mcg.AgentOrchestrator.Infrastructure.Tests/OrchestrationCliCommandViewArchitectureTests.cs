using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class OrchestrationCliCommandViewArchitectureTests
{
    private const string OrchestrationNamespace = "Mcg.AgentOrchestrator.App.Orchestration";
    private const string CliNamespace = "Mcg.AgentOrchestrator.App.Cli";
    private const string ReadModelsNamespace = "Mcg.AgentOrchestrator.App.ReadModels";
    private const string ExemptRootType = OrchestrationNamespace + ".BoardFillCliGoalIntake";

    // Sanctioned in-process CLI intake, already exempt in ConductorCliExecutorArchitectureTests.
    private static readonly string[] ExemptRoots = [ExemptRootType];

    private static DependencyIndex Index => DependencyIndexCache.Get(typeof(OrphanFixtureReaper).Assembly);

    [Fact]
    public void Orchestration_types_Cli_namespace_boundary_Has_no_dependencies()
    {
        var index = Index;
        var subjectTypes = Subjects(index, ExemptRoots);
        var forbiddenTypes = index.Types.Where(type => IsInCliNamespace(type.Type)).ToArray();

        Assert.Contains(subjectTypes, type => type.FullName == typeof(ConductorExperimentWatch).FullName);
        Assert.Contains(subjectTypes, type => type.FullName == typeof(StalledEpicAttention).FullName);
        Assert.Contains(subjectTypes, type => type.FullName == typeof(DefaultStateMover).FullName);
        Assert.Contains(subjectTypes, type => type.FullName == typeof(ConductorDriver).FullName);
        Assert.Contains(subjectTypes, type => type.FullName == typeof(OrphanFixtureReaper).FullName);
        Assert.DoesNotContain(subjectTypes, type => type.OutermostName == ExemptRootType);
        Assert.All(subjectTypes, type => Assert.Equal(OrchestrationNamespace, type.Namespace));
        Assert.Contains(forbiddenTypes, type => type.FullName == typeof(RepoProcessCliCommand).FullName);
        Assert.Contains(forbiddenTypes, type => type.FullName == typeof(GoalListTextView).FullName);
        Assert.Contains(forbiddenTypes, type => type.FullName == typeof(CliPersistentStateRunner).FullName);
        Assert.Contains(forbiddenTypes, type => type.FullName == typeof(CliExitException).FullName);
        Assert.Contains(forbiddenTypes, type => type.FullName == typeof(EpicProgressReadModel).FullName);
        Assert.DoesNotContain(forbiddenTypes, type => type.FullName == typeof(EpicPlanStatusReader).FullName);
        Assert.DoesNotContain(forbiddenTypes, type => type.FullName == typeof(ExperimentReading).FullName);
        Assert.DoesNotContain(forbiddenTypes, type => type.FullName == typeof(SystemConductorLockProbe).FullName);

        var failures = CliBoundaryRule(index, subjectTypes);
        Assert.True(failures.Length == 0, failures.ToErrorMessage());
    }

    [Fact]
    public void Empty_exemptions_Control_names_exactly_board_fill_intake()
    {
        var index = Index;
        var failures = CliBoundaryRule(index, Subjects(index, []));

        Assert.NotEmpty(failures);
        var failingSubjects = failures
            .Select(result => result.Subject.OutermostName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(failingSubjects.SetEquals([ExemptRootType]), failures.ToErrorMessage());
    }

    [Fact]
    public void Moved_types_Namespace_placement_Matches_owner_layer()
    {
        System.Type[] readModels =
        [
            typeof(EpicPlanStatusReader), typeof(EpicPlanView), typeof(EpicPlanItemStatus),
            typeof(EpicPlanNextStep), typeof(EpicPlanGoalClaimSnapshot), typeof(EpicPlanSourceFacts),
            typeof(EpicOutcomeEvaluator), typeof(EpicProgressBucket), typeof(ExperimentReading),
            typeof(ExperimentGoals), typeof(ExperimentAlternatingGateReading), typeof(ExperimentGateAttempts),
            typeof(ExperimentGateStopCount), typeof(ExperimentHostLedgers), typeof(ExperimentMembership),
            typeof(ExperimentCohortReading), typeof(ExperimentTwinReading), typeof(ExperimentHoursToLand),
            typeof(ExperimentIncrementalStopCount), typeof(CliOwnerDigestRetryIntents)
        ];
        Assert.All(readModels, type => Assert.Equal(ReadModelsNamespace, type.Namespace));

        System.Type[] orchestration =
        [
            typeof(IConductorLockProbe), typeof(ConductorLockOwnership), typeof(SystemConductorLockProbe),
            typeof(CriticalDispatchLifecycleCheckpoint), typeof(CriticalDispatchLifecycleEventGate)
        ];
        Assert.All(orchestration, type => Assert.Equal(OrchestrationNamespace, type.Namespace));
        Assert.Equal(CliNamespace, typeof(GoalRefinementWorkOutcomeReporter).Namespace);
        Assert.Equal(CliNamespace, typeof(FleetAttentionReader).Namespace);
    }

    [Fact]
    public void ReadModels_types_Cli_dependencies_Residual_is_single_progress_call()
    {
        var pairs = Index.Types
            .Where(type => type.Namespace == ReadModelsNamespace)
            .SelectMany(type => type.References.Where(IsInCliNamespace)
                .Select(target => (Source: type.OutermostName, Target: target.OutermostName)))
            .ToHashSet();

        Assert.True(pairs.SetEquals(
            [(typeof(EpicPlanStatusReader).FullName!, typeof(EpicProgressReadModel).FullName!)]),
            "ReadModels-to-Cli dependencies: " + string.Join(", ", pairs.OrderBy(pair => pair.Source)
                .ThenBy(pair => pair.Target).Select(pair => $"{pair.Source} -> {pair.Target}")));
    }

    private static DependencyDeclaration[] Subjects(DependencyIndex index, IReadOnlyCollection<string> exemptRoots) =>
        index.Types.Where(type => type.Namespace == OrchestrationNamespace
            && !exemptRoots.Contains(type.OutermostName, StringComparer.Ordinal)).ToArray();

    private static DependencyViolation[] CliBoundaryRule(DependencyIndex index, DependencyDeclaration[] subjects) =>
        DependencyIndexRules.ForbiddenTypes(index, subjects, IsInCliNamespace);

    private static bool IsInCliNamespace(DependencyType type) =>
        type.Namespace == CliNamespace || type.Namespace.StartsWith(CliNamespace + ".", StringComparison.Ordinal);
}
