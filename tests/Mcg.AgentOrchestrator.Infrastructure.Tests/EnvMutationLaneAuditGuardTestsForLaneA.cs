using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Reads candidate source and assembly metadata only, so it can run in parallel.
public sealed class EnvMutationLaneAuditGuardTestsForLaneA
{
    private const string AuditedLane = "Worker dispatch fixtures A";
    private const string TestPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/";
    private const string DispatchSupport = TestPath + "WorkerDispatchTests.cs";
    private const string InfrastructureSupport = TestPath + "InfrastructureTestSupport.cs";

    private static readonly string[] TerminalContractFilesRead =
    [
        DispatchSupport,
        InfrastructureSupport,
        TestPath + "InfrastructureTestSupportGitCommit.cs",
        TestPath + "InfrastructureTestSupportGitProbeSeam.cs",
        TestPath + "InfrastructureTestSupportStateSetup.cs",
        TestPath + "WorkerDispatchTestsSeededRepositoryFactory.cs",
        TestPath + "WorkerDispatchTestsSeededRepositoryFactory.TemplateCache.cs",
        TestPath + "TestOwnedDispatchProcesses.cs",
        TestPath + "AssemblyTempRedirect.cs",
        "tests/Mcg.AgentOrchestrator.TestSupport/SharedTestSupport.cs",
        "src/Mcg.AgentOrchestrator.Execution/Processes/BackgroundDispatchRunner.cs",
        "src/Mcg.AgentOrchestrator.Execution/Processes/ProtectedProcessIdentity.cs",
        "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalWorktrees.Create.cs",
        "src/Mcg.AgentOrchestrator.App/Program.cs",
        "src/Mcg.AgentOrchestrator.App/Cli/CliProtectedProcessEnvironment.cs",
        "src/Mcg.AgentOrchestrator.App/Cli/CliTimingRecorder.cs",
        "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorContinuitySupervisor.ProtectedIdentity.cs",
        "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorSupervisorHandoffSeam.cs",
        "src/Mcg.AgentOrchestrator.App/Orchestration/OrchestratorDataRoot.cs",
        "src/Mcg.AgentOrchestrator.App/Orchestration/OrchestratorHome.cs",
    ];

    private static readonly AuditRow[] Audit =
    [
        // Follow-up candidate. These scopes change four variables and restore them on disposal.
        new(nameof(WorkerDispatchTestsWorkerResultClassification), [],
        [
            new("ClearWorkerSandboxEnv",
                new Citation(DispatchSupport, 506, 506,
                    "Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, null)"),
                new Citation(DispatchSupport, 513, 513,
                    "Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, previous)")),
            new("ClearProtectedPidEnvironment",
                new Citation(InfrastructureSupport, 659, 660,
                    "Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, null)",
                    "Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedStartTicksVariable, null)"),
                new Citation(InfrastructureSupport, 804, 806,
                    "Environment.SetEnvironmentVariable(variableName, previousValue)",
                    "Environment.SetEnvironmentVariable(secondVariableName, secondPreviousValue)")),
            new("UseFixtureIsolatedDotnetRoot",
                new Citation(DispatchSupport, 2065, 2065,
                    "Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _root)"),
                new Citation(DispatchSupport, 2070, 2070,
                    "Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _previousValue)")),
        ]),
        // none. The class file and its dispatch, repository, and lifecycle paths were read.
        new(nameof(WorkerDispatchTestsSubscriptionTerminalContract), TerminalContractFilesRead, []),
        // none. The submission case also reaches the in-process submission and ingestion paths.
        new(nameof(WorkerDispatchTestsSubscriptionTerminalContractSubmission),
        [
            .. TerminalContractFilesRead,
            TestPath + "WorkerResultSubmissionTests.cs",
            "src/Mcg.AgentOrchestrator.Execution/Workers/WorkerResultSubmission.cs",
            "src/Mcg.AgentOrchestrator.Execution/Workers/WorkerResultSubmissionIngestion.cs",
        ], []),
    ];

    private static readonly (string Lane, Type[] Candidates)[] UnauditedFollowUps =
    [
        ("Worker profiles",
        [
            typeof(AdvanceLoopTests),
            typeof(AdvanceLoopTestsBlockingWorkerRelease),
            typeof(AdvanceLoopTestsDispatchProcessLeakGuard),
            typeof(ConductLoopLockTests),
            typeof(ConductorLoopHandoffTests),
            typeof(GoalBacklogLinkTests),
            typeof(Mcg.AgentOrchestrator.Infrastructure.Tests.GoalLifecycleEventWriterTests),
            typeof(Mcg.AgentOrchestrator.Infrastructure.Tests.GoalLifecycleEventWriterTestsLandingAdmission),
            typeof(SqliteOrchestratorStateRepositoryTests),
            typeof(SqliteOrchestratorStateRepositoryTestsCrossProviderRelease),
            typeof(WorkerProfileTests),
            typeof(WorkspaceConsolidatorTests),
        ]),
        ("Worker dispatch fixtures B",
        [
            typeof(WorkerDispatchTestsSandboxLowIntegrity),
            typeof(WorkerDispatchTestsSubscriptionPreflight),
        ]),
        ("Worker dispatch fixtures C",
        [
            typeof(DispatchHostLifetimeHandoffTests),
            typeof(DispatchHostLifetimeHandoffTestsIdentityPolicy),
            typeof(DispatchHostLifetimeHandoffTestsWaitMessage),
            typeof(RealWorkerProcessGuardTests),
            typeof(WorkerDispatchTestsDispatchPreparation),
            typeof(WorkerDispatchTestsDispatchPreparationClaudeAuthProbeInjectionTests),
            typeof(WorkerDispatchTestsDispatchPreparationClaudeCredentials),
            typeof(WorkerDispatchTestsDispatchPreparationHostCommandProbe),
            typeof(WorkerDispatchTestsModelSelectionEnvMutation),
            typeof(WorkerProcessJobsTests),
            typeof(WorkerProcessJobsTestsBuildOutputLocator),
        ]),
    ];

    [Fact]
    public void Audit_agrees_with_lane_membership_collection_and_candidate_source()
    {
        var root = VerifiedRepositoryRoot.Find();
        var lanes = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes;
        var inAuditedLane = LaneMatcher(lanes, AuditedLane);
        var runnable = GetType().Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && type.GetMethods().Any(method =>
                method.GetCustomAttributes(inherit: true).OfType<Xunit.v3.IFactAttribute>().Any()))
            .ToDictionary(type => type.FullName!, StringComparer.Ordinal);
        var selected = runnable.Where(entry => inAuditedLane(entry.Key)).ToDictionary(entry => entry.Key,
            entry => entry.Value.GetCustomAttributes(inherit: true).OfType<Xunit.v3.ICollectionAttribute>()
                .Any(attribute => attribute.Name == TestCollections.EnvMutation ||
                    attribute.Type == typeof(EnvMutationCollection)), StringComparer.Ordinal);

        CheckAuditSources(root, selected);
        foreach (var (lane, candidates) in UnauditedFollowUps)
        {
            var inLane = LaneMatcher(lanes, lane);
            foreach (var candidate in candidates)
            {
                var name = candidate.FullName!;
                Assert.True(runnable.ContainsKey(name) && inLane(name) && !selected.ContainsKey(name),
                    $"{name} must be a runnable follow-up in {lane} outside {AuditedLane}.");
            }
        }
    }

    [Theory]
    [InlineData("consistent", null)]
    [InlineData("missing", "missing audit row")]
    [InlineData("duplicate", "duplicate audit rows")]
    [InlineData("outside lane", "outside the audited lane")]
    [InlineData("none in collection", "recorded none")]
    [InlineData("writer outside collection", "without EnvMutation membership")]
    [InlineData("stale citation", "Writes.cs:1-1")]
    [InlineData("invalid range", "Writes.cs:1-2")]
    [InlineData("commented citation", "no matching write")]
    [InlineData("disconnected helper", "no call to Write")]
    public void Audit_detects_disagreement(string scenario, string? expected)
    {
        const string call = """Environment.SetEnvironmentVariable("name", null)""";
        var write = new Mutation("Write", new Citation("Writes.cs", 1, scenario == "invalid range" ? 2 : 1, call));
        var row = new AuditRow("Example", [], scenario == "none in collection" ? [] : [write]);
        AuditRow[] rows = scenario switch
        {
            "missing" => [],
            "duplicate" => [row, row],
            "outside lane" => [row, row with { ClassName = "OtherExample" }],
            _ => [row],
        };
        var selected = new Dictionary<string, bool> { ["Example"] = scenario != "writer outside collection" };
        var classText = scenario == "disconnected helper" ? "class Example { }" : "class Example { void Run() => Write(); }";
        var helperText = scenario switch
        {
            "stale citation" => $"\nclass Helper {{ void Write() => {call}; }}",
            "commented citation" => $"class Helper {{ /* {call} */ void Write() {{ }} }}",
            _ => $"class Helper {{ void Write() => {call}; }}",
        };
        var sources = new Dictionary<string, SyntaxNode?>
        {
            [row.SourcePath] = CSharpSyntaxTree.ParseText(classText).GetRoot(),
            ["Writes.cs"] = CSharpSyntaxTree.ParseText(helperText).GetRoot(),
        };

        var failures = FindDisagreements(rows, selected, sources).ToArray();
        if (expected is null)
            Assert.Empty(failures);
        else
            Assert.Contains(expected, Assert.Single(failures), StringComparison.Ordinal);
    }

    private static Func<string, bool> LaneMatcher(IReadOnlyList<AcceptanceTestLane> lanes, string name) =>
        AcceptanceLaneMembership.CreateClassMatcher(Assert.Single(lanes, lane => lane.Name == name));

    private static void CheckAuditSources(string root, IReadOnlyDictionary<string, bool> selected)
    {
        foreach (var path in Audit.SelectMany(row => row.FilesRead).Distinct(StringComparer.Ordinal))
            Assert.True(File.Exists(Path.Combine(root, path)), $"Audit source file is missing: {path}");

        var sources = Audit.Select(row => row.SourcePath)
            .Concat(Audit.SelectMany(row => row.Mutations).SelectMany(mutation => mutation.Citations).Select(citation => citation.Path))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(path => path, path => ReadSource(root, path), StringComparer.Ordinal);
        var failures = FindDisagreements(Audit, selected, sources).ToArray();
        Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));
    }

    private static SyntaxNode? ReadSource(string root, string path)
    {
        var fullPath = Path.Combine(root, path);
        return File.Exists(fullPath) ? CSharpSyntaxTree.ParseText(File.ReadAllText(fullPath)).GetRoot() : null;
    }

    private static IEnumerable<string> FindDisagreements(IReadOnlyList<AuditRow> rows,
        IReadOnlyDictionary<string, bool> selected, IReadOnlyDictionary<string, SyntaxNode?> sources)
    {
        var byClass = rows.ToLookup(row => row.ClassName, StringComparer.Ordinal);
        foreach (var className in byClass.Select(group => group.Key).Except(selected.Keys, StringComparer.Ordinal))
            yield return $"{className} is outside the audited lane.";
        foreach (var (className, inCollection) in selected)
        {
            var findings = byClass[className].ToArray();
            if (findings.Length != 1)
            {
                yield return findings.Length == 0 ? $"{className} is missing audit row."
                    : $"{className} has duplicate audit rows.";
                continue;
            }

            var row = findings[0];
            if (row.Mutations.Length == 0 && inCollection)
                yield return $"{className} recorded none but still has EnvMutation membership.";
            if (row.Mutations.Length > 0 && !inCollection)
                yield return $"{className} has cited writes without EnvMutation membership.";
            foreach (var failure in CheckFinding(row, rows, sources))
                yield return failure;
        }
    }

    private static IEnumerable<string> CheckFinding(AuditRow row, IReadOnlyList<AuditRow> rows,
        IReadOnlyDictionary<string, SyntaxNode?> sources)
    {
        var classSource = sources.GetValueOrDefault(row.SourcePath)?.DescendantNodes()
            .OfType<ClassDeclarationSyntax>().SingleOrDefault(type => type.Identifier.ValueText == row.SimpleName);
        if (classSource is null)
        {
            yield return $"{row.SourcePath} does not contain {row.ClassName}.";
            yield break;
        }

        var calls = classSource.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(call => MemberName(call.Expression)).ToHashSet(StringComparer.Ordinal);
        if (row.Mutations.Length == 0)
        {
            var helpers = rows.SelectMany(other => other.Mutations).Select(mutation => mutation.Helper);
            if (classSource.DescendantNodes().Any(IsProcessWrite) || helpers.Any(calls.Contains))
                yield return $"{row.ClassName} recorded none but calls a process-wide write or a cited mutation helper.";
        }
        foreach (var mutation in row.Mutations)
        {
            if (!calls.Contains(mutation.Helper))
                yield return $"{row.SourcePath} has no call to {mutation.Helper}.";
            foreach (var citation in mutation.Citations)
            {
                foreach (var expectedCall in citation.Calls)
                {
                    var error = CheckCitation(citation, expectedCall, sources.GetValueOrDefault(citation.Path));
                    if (error is not null)
                        yield return $"{row.ClassName} via {mutation.Helper}: {error}";
                }
            }
        }
    }

    private static string? CheckCitation(Citation citation, string call, SyntaxNode? source)
    {
        var locations = source?.DescendantNodes()
            .Where(node => IsProcessWrite(node) && node.ToString() == call)
            .Select(node => node.GetLocation().GetLineSpan())
            .Select(span => (Start: span.StartLinePosition.Line + 1, End: span.EndLinePosition.Line + 1))
            .ToArray() ?? [];
        if (source is not null && citation.Start >= 1 && citation.End <= source.SyntaxTree.GetText().Lines.Count &&
            locations.Any(location => location.Start >= citation.Start && location.End <= citation.End))
            return null;

        var current = locations.Length == 0 ? "no matching write in source"
            : "matching write at " + string.Join(", ", locations.Select(location => $"{citation.Path}:{location.Start}-{location.End}"));
        return $"{citation.Path}:{citation.Start}-{citation.End} must contain {call} ({current}).";
    }

    private static bool IsProcessWrite(SyntaxNode node) => node switch
    {
        InvocationExpressionSyntax call => MemberName(call.Expression) is "SetEnvironmentVariable" or "SetCurrentDirectory",
        AssignmentExpressionSyntax assignment => MemberName(assignment.Left) == "CurrentDirectory",
        _ => false,
    };

    private static string? MemberName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null,
    };

    private sealed record AuditRow(string ClassName, string[] FilesRead, Mutation[] Mutations)
    {
        public string SimpleName => ClassName.Split('.', '+')[^1];
        public string SourcePath => TestPath + SimpleName + ".cs";
    }

    private sealed record Mutation(string Helper, params Citation[] Citations);
    private sealed record Citation(string Path, int Start, int End, params string[] Calls);
}
