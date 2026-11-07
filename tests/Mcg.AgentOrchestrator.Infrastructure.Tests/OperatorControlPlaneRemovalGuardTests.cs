using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.Core;
using System.Text.RegularExpressions;

// Parallel-safe: repository source reads only; no shared state is changed.
public sealed class OperatorControlPlaneRemovalGuardTests
{
    private static readonly string[] ForbiddenLiterals =
    [
        "operator-control-plane",
        "HandleOperatorControlPlaneCommand",
        "ControlPlaneReplayHarness",
        "ControlPlaneReplayReport",
        "OperatorInboxControlPlaneProjection",
        "ControlPlaneBacklogDigestItem",
        "DeliverDailyBacklogDigestAsync",
        "RenderDailyBacklogDigest",
        "DailyBacklogDigestKey",
        "control-plane-replay",
        "ControlPlaneMutedUntil",
        "ControlPlaneMuteControl",
        "BacklogDigestCadence",
        "ControlPlaneDeliveryPolicy",
        "control-plane-delivery",
        "DiscordGatewayListener",
        "DiscordOperatorChannel",
        "DiscordNetForumApi",
        "operator-listen",
        "Discord.Net"
    ];

    [Fact]
    public void SourceFiles_RemovedLiterals_HaveNoControlPlaneReplayRemnants()
    {
        var root = VerifiedRepositoryRoot.Find();
        var snapshot = RepositorySourceSnapshotCache.Get(root);
        var files = SourceFiles(root).ToArray();
        Assert.NotEmpty(files);
        var violations = files.SelectMany(file =>
            FindForbiddenLiterals(snapshot.GetText(file)).Select(literal =>
                $"'{literal}' found in {Path.GetRelativePath(root, file)}")).ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void FindForbiddenLiterals_ReplayHarness_ReportsCaseInsensitiveMatchOnce()
    {
        var matches = FindForbiddenLiterals("ControlPlaneReplayHarness controlplanereplayharness");

        Assert.Equal("ControlPlaneReplayHarness", Assert.Single(matches));
    }

    [Fact]
    public void FindForbiddenLiterals_DiscordGatewayListener_ReportsViolation()
    {
        var matches = FindForbiddenLiterals("class DiscordGatewayListener { }");

        Assert.Equal("DiscordGatewayListener", Assert.Single(matches));
    }

    [Fact]
    public void FindForbiddenLiterals_KeptDeliveryTypes_ReportsNoMatches()
    {
        var matches = FindForbiddenLiterals(
            "ComposeDailyBacklogDigest " +
            "DiscordControlPlaneDeliverer ControlPlaneDecisionCard");

        Assert.Empty(matches);
    }

    [Fact]
    public void RecognizedCommands_RemovedVerb_KeepsSiblingOperatorCommands()
    {
        var commands = CliArgumentParser.RecognizedCommands;

        Assert.DoesNotContain("operator-control-plane", commands, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("operator-channel", commands, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("operator-listen", commands, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("operator-inbox", commands, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assembly_RemovedDeliveryTypes_AreAbsentAndKeptTypesRemain()
    {
        var assemblies = new[]
        {
            typeof(AgentOrchestratorKernel).Assembly,
            typeof(GoalAcceptanceVerifier).Assembly,
            typeof(CohortAcceptanceStore).Assembly,
            typeof(ProviderSmokeTester).Assembly,
            typeof(CliArgumentParser).Assembly
        };
        string[] removedTypes =
        [
            "IOperatorChannel",
            "NullOperatorChannel",
            "OperatorChannelCatalog",
            "OperatorChannelStore",
            "OperatorChannelFactory",
            "DeadManHeartbeatOptions",
            "OperatorDecision",
            "ControlPlaneCardSource",
            "ControlPlaneAction",
            "IControlPlaneMessageTransport",
            "RecordingControlPlaneMessageTransport",
            "ControlPlaneDecisionCard",
            "ControlPlaneDeliveryChannel",
            "DeadManHeartbeatClient",
            "DiscordControlPlaneDeliverer",
            "IControlPlaneDeliveryStore",
            "InMemoryControlPlaneDeliveryStore",
            "SqliteControlPlaneDeliveryStore",
            "DiscordControlPlaneMessageTransport",
            "DiscordControlPlaneMessageTransportOptions",
            "ControlPlaneDeliveryOperationKind",
            "ControlPlaneBoardSnapshot",
            "ControlPlaneDeliveryPolicy",
            "SystemicStormDeliveryState",
            "ControlPlaneDeliveryMark",
            "ControlPlanePushReceipt",
            "ControlPlaneDeliveryOperation",
            "ControlPlaneDeliveryBatchResult",
            "ControlPlaneMuteControl",
            "StewardPrecedentKey",
            "StewardBriefingBundleInputs",
            "StewardBriefingBundleProducer",
            "IStewardTriageEngine",
            "StewardComposer",
            "StewardDispatcher",
            "StewardWakeReason",
            "StewardWakeDecisionKind",
            "StewardOutputKind",
            "StewardEscalationCategory",
            "StewardDispositionKind",
            "StewardDispatchOptions",
            "StewardWakeSignal",
            "StewardWakeDecision",
            "StewardWakeScheduler",
            "StewardEscalationItem",
            "StewardGoalTaskRecord",
            "StewardNumericReceiptSummary",
            "StewardPrecedentMatch",
            "StewardPolicySnapshot",
            "StewardInterruptBudgetLedger",
            "StewardProvenanceLink",
            "StewardQuotedWorkerProse",
            "StewardBriefingBundle",
            "StewardBacklogCandidate",
            "StewardRankedBacklogItem",
            "StewardDailyBacklogDigest",
            "StewardDailyBrief",
            "StewardAutonomousAction",
            "StewardBoardStateDiff",
            "StewardCatchUpReplay",
            "StewardStormCollapseJudgment",
            "StewardHeartbeat",
            "StewardCardLoadMeasurement",
            "StewardOutput`1",
            "StewardTriageBatch",
            "StewardDispatchResult",
            "StewardInboxDisposition",
            "StewardTriageReceipt",
            "StewardBypassPolicy",
            "StewardHeartbeatCalculator",
            "StewardInputHasher",
            "IStewardTriageReceiptStore",
            "InMemoryStewardTriageReceiptStore",
            "SqliteStewardTriageReceiptStore",
            "SqliteStewardTriageReceiptStore+StoredDisposition"
        ];
        foreach (var assembly in assemblies)
        {
            foreach (var name in removedTypes)
                Assert.Null(assembly.GetType("Mcg.AgentOrchestrator.Infrastructure." + name));
            Assert.Null(assembly.GetType("Mcg.AgentOrchestrator.App.Orchestration.OperatorChannelComposition"));
        }

        var app = typeof(CliArgumentParser).Assembly;
        Assert.NotNull(app.GetType("Mcg.AgentOrchestrator.Infrastructure.OperatorEscalation"));
        Assert.NotNull(app.GetType("Mcg.AgentOrchestrator.Infrastructure.OperatorEscalationAction"));
        Assert.DoesNotContain(StoreSchemaRegistry.Inventory, entry => entry.StoreName == "control-plane-delivery");
        Assert.DoesNotContain(StoreSchemaRegistry.Inventory, entry => entry.StoreName == "steward-triage-receipts");
    }

    [Fact]
    public void Solution_OperatorCommsProject_IsAbsent()
    {
        var root = VerifiedRepositoryRoot.Find();
        var projects = SolutionProjects(File.ReadAllText(Path.Combine(root, "Mcg.AgentOrchestrator.sln")));
        Assert.NotEmpty(projects);
        Assert.DoesNotContain(projects, project =>
            project.Name.Equals("Mcg.AgentOrchestrator.Infrastructure.OperatorComms", StringComparison.OrdinalIgnoreCase) ||
            project.Path.Contains("Mcg.AgentOrchestrator.Infrastructure.OperatorComms", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SourceFiles_IOperatorChannel_IsNeitherDeclaredNorReferenced()
    {
        var root = VerifiedRepositoryRoot.Find();
        var snapshot = RepositorySourceSnapshotCache.Get(root);
        var files = SourceFiles(root).ToArray();
        Assert.NotEmpty(files);
        var violations = files.Where(file => ReferencesOperatorChannel(snapshot.GetText(file)))
            .Select(file => Path.GetRelativePath(root, file)).ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void SolutionProjectNames_OperatorCommsEntry_IsDetected()
    {
        var projects = SolutionProjects(
            """
            Project("{TYPE}") = "Mcg.AgentOrchestrator.Infrastructure.OperatorComms", "src\Mcg.AgentOrchestrator.Infrastructure.OperatorComms\Mcg.AgentOrchestrator.Infrastructure.OperatorComms.csproj", "{ID}"
            """);
        var project = Assert.Single(projects);
        Assert.Equal("Mcg.AgentOrchestrator.Infrastructure.OperatorComms", project.Name);
        Assert.Contains("Mcg.AgentOrchestrator.Infrastructure.OperatorComms", project.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void ReferencesOperatorChannel_DeclarationText_IsDetected()
    {
        Assert.True(ReferencesOperatorChannel("public interface IOperatorChannel { }"));
        Assert.True(ReferencesOperatorChannel("private IOperatorChannel? channel;"));
        Assert.True(ReferencesOperatorChannel("// IOperatorChannel was removed"));
        Assert.False(ReferencesOperatorChannel("public sealed record OperatorEscalation(string Title);"));
    }

    private static IReadOnlyList<(string Name, string Path)> SolutionProjects(string solution) =>
        Regex.Matches(solution, @"^Project\([^\r\n]+?\) = ""([^""]+)"", ""([^""]+)""", RegexOptions.Multiline)
            .Select(match => (match.Groups[1].Value, match.Groups[2].Value)).ToArray();

    private static bool ReferencesOperatorChannel(string sourceText) =>
        sourceText.Contains("IOperatorChannel", StringComparison.Ordinal);

    internal static IReadOnlyList<string> FindForbiddenLiterals(string sourceText) =>
        ForbiddenLiterals.Where(literal =>
            sourceText.Contains(literal, StringComparison.OrdinalIgnoreCase)).ToArray();

    internal static IEnumerable<string> SourceFiles(string root) =>
        RepositorySourceSnapshotCache.Get(root).EnumerateFiles(Path.Combine(root, "src"))
            .Where(file => !SourceGuardFileFilter.WorkspaceAndReports.Excludes(Path.GetRelativePath(root, file)));
}
