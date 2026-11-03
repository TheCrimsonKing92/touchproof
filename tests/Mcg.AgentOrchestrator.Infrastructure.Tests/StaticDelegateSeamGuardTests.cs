using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Parallel-safe: source reads and invocation-local fakes; filesystem work uses a unique directory.
public sealed class StaticDelegateSeamGuardTests
{
    private static readonly Seam[] AllowList =
    [
        new("src/Mcg.AgentOrchestrator.Execution/Processes/ChildConsoleLaunchPolicy.cs", "Mcg.AgentOrchestrator.Infrastructure.ChildConsoleLaunchPolicy", "PrepareDelayHookForTests"),
        new("src/Mcg.AgentOrchestrator.Execution/Processes/LockAttribution.cs", "Mcg.AgentOrchestrator.Infrastructure.LockAttribution", "AttributeForTests"),
        new("src/Mcg.AgentOrchestrator.Execution/Processes/LockAttribution.cs", "Mcg.AgentOrchestrator.Infrastructure.LockAttribution", "ConfigureHandleProbeForTests"),
        new("src/Mcg.AgentOrchestrator.Execution/Processes/WorkerProcessJobs.cs", "Mcg.AgentOrchestrator.Infrastructure.WorkerProcessJobs", "TryKillPidTree"),
        new("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalChangesReader.cs", "Mcg.AgentOrchestrator.Infrastructure.GoalChangesReader", "RunGit"),
        new("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalWorktrees.AcceptanceCohorts.cs", "Mcg.AgentOrchestrator.Infrastructure.AcceptanceCohortWorkspace", "WorkspaceRemover"),
        new("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/SourceSizeRatchetRetightener.cs", "Mcg.AgentOrchestrator.Infrastructure.SourceSizeRatchetRetightener", "ProgressSink"),
        new("src/Mcg.AgentOrchestrator.App/Cli/CliGoalReplacementExecution.cs", "Mcg.AgentOrchestrator.App.Cli.CliGoalReplacementExecution", "AfterGoalReplacementLeaseAcquisitionFailed"),
        new("src/Mcg.AgentOrchestrator.App/Cli/CliGoalReplacementExecution.cs", "Mcg.AgentOrchestrator.App.Cli.CliGoalReplacementExecution", "BeforeGoalReplacementFailureClaimObservation"),
        new("src/Mcg.AgentOrchestrator.App/Cli/ProjectInstallManifestCommand.cs", "Mcg.AgentOrchestrator.App.Cli.ProjectInstallManifestCommand", "ReplaceFaultForTests"),
        new("src/Mcg.AgentOrchestrator.App/Orchestration/GoalRefinementWorkCoordinator.cs", "Mcg.AgentOrchestrator.App.Orchestration.GoalRefinementWorkCoordinator", "LaunchOverride"),
    ];

    [Fact]
    public void SourceContainsExactlyTheAllowListedStaticDelegateSeams()
    {
        var root = VerifiedRepositoryRoot.Find();
        var snapshot = RepositorySourceSnapshotCache.Get(root);
        var trees = snapshot.EnumerateFiles("src")
            .Where(path => !SourceGuardFileFilter.WorkspaceAndReports.Excludes(Path.GetRelativePath(root, path)))
            .Select(path => (Path.GetRelativePath(root, path).Replace('\\', '/'), snapshot.GetSyntaxTree(path)))
            .ToArray();

        Assert.Equal(11, AllowList.Length);
        Assert.Equal(AllowList.Length, AllowList.Distinct().Count());
        Assert.Empty(AllowListViolations(Scan(trees), AllowList));
    }

    [Fact]
    public void AddingAnUnlistedStaticDelegateFailsTheSameAllowListValidation()
    {
        const string path = "src/Fixture.cs";
        const string original = "class Fixture { public static System.Action Existing { get; set; } }";
        Seam[] allowed = [new(path, "Fixture", "Existing")];
        var baseline = Scan((path, CSharpSyntaxTree.ParseText(original)));
        Assert.Empty(AllowListViolations(baseline, allowed));

        var added = CSharpSyntaxTree.ParseText(original +
            " class Added { public static System.Func<int> Runner { get; private set; } }");
        var violations = AllowListViolations(Scan((path, added)), allowed);
        Assert.Equal($"Unexpected: {path}::Added.Runner", Assert.Single(violations));
        Assert.Equal($"Stale: {path}::Fixture.Existing", Assert.Single(AllowListViolations([], allowed)));
    }

    [Fact]
    public void ScanRecognizesDelegateTypesAndSettersAcrossSourceForms()
    {
        const string source = """
            using System;
            using Callback = System.Action;
            using Runner = System.Func<int>;
            using Runtime = System;
            namespace Fixture;
            class Hooks
            {
                public static Func<int> Generic { get; set; }
                internal static Action Plain { get; private set; }
                public static System.Action<int>? Qualified { get; internal set; }
                public static global::System.Func<int> Global { get; set; }
                public static Callback? Aliased { get; set; }
                public static Runner AliasedGeneric { get; set; }
                public static Runtime.Action NamespaceAlias { get; set; }
                public static Action
                    Multiline
                    { get; private set; }
                public static Action Bodied { get => null; set { } }
                public static Action Field;
                public static Action ReadOnly { get; }
                public static Action Expression => null;
                public Action Instance { get; set; }
            }
            class Other { public static int Number { get; set; } }
            class Shadow
            {
                class Action { }
                public static Action NotSystemAction { get; set; }
            }
            """;
        var seams = Scan(("src/Fixture.cs", CSharpSyntaxTree.ParseText(source)));
        Assert.Equal(new[]
        {
            "Aliased", "AliasedGeneric", "Bodied", "Generic", "Global", "Multiline",
            "NamespaceAlias", "Plain", "Qualified"
        }, seams.Select(seam => seam.Property).Order(StringComparer.Ordinal));
        Assert.All(seams, seam => Assert.Equal("Fixture.Hooks", seam.Type));
    }

    [Fact]
    public void GoalGitFactIndexRunsBuildCommandsThroughInjectedRunner()
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var calls = new List<string>();
        GitCli.GitResult Fake(string directory, IReadOnlyList<string> args)
        {
            Assert.Equal(root, directory);
            var command = string.Join(" ", args);
            calls.Add(command);
            return Success(command switch
            {
                "for-each-ref --format=%(refname:short) %(objectname) refs/heads/goal/" => "goal/example branch-sha\n",
                "rev-parse --verify refs/heads/main" => "main-sha\n",
                _ => string.Empty
            });
        }

        var index = GoalGitFactIndex.Build(root, "main", gitRunner: Fake);

        Assert.Equal(new[]
        {
            "for-each-ref --format=%(refname:short) %(objectname) refs/heads/goal/",
            "for-each-ref --format=%(refname:short) --merged HEAD refs/heads/goal/",
            "worktree list --porcelain",
            "rev-parse --verify refs/heads/main"
        }, calls);
        Assert.True(index.IsAvailable);
        Assert.True(index.HasGoalBranch("goal/example"));
        Assert.Equal("main-sha", index.MainSha);
    }

    [Fact]
    public void GoalIntegrationEvidenceResolverUsesInjectedRunnerForBuildAndAncestry()
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var goalId = new GoalId("aaaaaaaa111111111111111111111111");
        var calls = new List<string>();
        GitCli.GitResult Fake(string directory, IReadOnlyList<string> args)
        {
            Assert.Equal(root, directory);
            var command = string.Join(" ", args);
            calls.Add(command);
            return Success(command switch
            {
                "rev-parse --verify refs/heads/main" => "main-sha\n",
                "log main-sha --format=%H%x09%s --grep=^Integrate goal/" =>
                    $"integrate-sha\tIntegrate goal/{goalId.Value[..8]}\n",
                _ => string.Empty
            });
        }

        var resolver = GoalIntegrationEvidenceResolver.Build(root, "main", gitRunner: Fake);

        Assert.True(resolver.TryResolve(goalId, out var evidence));
        Assert.Equal(new[]
        {
            "rev-parse --verify refs/heads/main",
            "log main-sha --format=%H%x09%s --grep=^Integrate goal/",
            "merge-base --is-ancestor integrate-sha main-sha"
        }, calls);
        Assert.Equal(new GoalIntegrationEvidence("integrate-sha", "main-sha", $"Integrate goal/{goalId.Value[..8]}"), evidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProgressiveReviewGlanceReadsEveryDiffThroughInjectedRunner(bool throughConstructor)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-injected-glance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "new.txt"), "untracked content\n");
            var calls = new List<string>();
            GitCli.GitResult Fake(string directory, string[] args)
            {
                Assert.Equal(root, directory);
                var command = string.Join(" ", args);
                calls.Add(command);
                return Success(command switch
                {
                    "diff base..HEAD --" => "committed content",
                    "diff --" => "working content",
                    "diff --cached --" => "staged content",
                    "ls-files --others --exclude-standard" => "new.txt\n",
                    _ => throw new InvalidOperationException($"Unexpected git command: {command}")
                });
            }

            string diff;
            if (throughConstructor)
            {
                var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
                var kernel = new AgentOrchestratorKernel();
                var task = new TaskSpec(new TaskId("injected-git-task"), "Implement feature.", AgentRole.Developer);
                var goal = kernel.CreateGoal(new GoalId("injected-git-goal"), "Review injected diff", [task]);
                kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
                kernel.RecordTaskDispatch(goal.Id, task.Id,
                    new TaskDispatchRecord("codex-cli", "codex exec", root, now, BaseCommit: "base"));
                var reviewer = new CapturingGlanceRunner();
                var coordinator = new ProgressiveReviewGlanceCoordinator(
                    reviewer, NullGoalLifecycleEventWriter.Instance,
                    new CollaborationItemStore(Path.Combine(root, "items.db")),
                    new ProgressiveReviewGlanceOptions(ChangedFileThreshold: 1),
                    utcNow: () => now,
                    liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["new.txt"], ["new.txt"], 0),
                    transcriptReader: _ => "transcript",
                    gitRunner: Fake);

                _ = coordinator.Observe(kernel, [goal]);

                diff = Assert.Single(reviewer.Calls).DiffExcerpt;
            }
            else
            {
                diff = ProgressiveReviewGlanceCoordinator.ReadDiff(root, "base", Fake);
            }

            Assert.Equal(new[]
            {
                "diff base..HEAD --", "diff --", "diff --cached --", "ls-files --others --exclude-standard"
            }, calls);
            Assert.StartsWith("working content" + Environment.NewLine + "staged content", diff);
            Assert.Contains("+untracked content", diff);
            Assert.EndsWith("committed content", diff);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GitCli.GitResult Success(string output) => new(0, output, string.Empty);

    private sealed class CapturingGlanceRunner : IProgressiveReviewGlanceRunner
    {
        public List<ProgressiveReviewGlanceInputs> Calls { get; } = [];

        public Task<ProgressiveReviewGlanceDispatchResult> RunAsync(
            ProgressiveReviewGlanceInputs inputs, CancellationToken cancellationToken = default)
        {
            Calls.Add(inputs);
            return Task.FromResult(new ProgressiveReviewGlanceDispatchResult(
                ProgressiveReviewGlanceVerdict.OnTrack, "ok", "ok"));
        }
    }

    private static IReadOnlyList<Seam> Scan(params (string Path, SyntaxTree Tree)[] sources)
    {
        // Binding the type handles aliases (including namespace/global aliases) and avoids
        // mistaking a user-defined type named Action or Func for a System delegate.
        var compilation = CSharpCompilation.Create("StaticDelegateSeamScan",
            sources.Select(source => source.Tree)
                .Append(CSharpSyntaxTree.ParseText("global using System;")),
            [MetadataReference.CreateFromFile(typeof(Action).Assembly.Location)]);
        var seams = new List<Seam>();
        foreach (var (path, tree) in sources)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var property in tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (!property.Modifiers.Any(SyntaxKind.StaticKeyword) ||
                    property.AccessorList?.Accessors.Any(accessor => accessor.IsKind(SyntaxKind.SetAccessorDeclaration)) != true ||
                    model.GetTypeInfo(property.Type).Type is not INamedTypeSymbol type ||
                    type.TypeKind != TypeKind.Delegate || type.Name is not ("Func" or "Action") ||
                    type.ContainingNamespace.ToDisplayString() != "System")
                    continue;

                var symbol = model.GetDeclaredSymbol(property)!;
                seams.Add(new Seam(path, symbol.ContainingType.ToDisplayString(), property.Identifier.ValueText));
            }
        }
        return seams;
    }

    private static IReadOnlyList<string> AllowListViolations(IEnumerable<Seam> discovered, IEnumerable<Seam> allowed)
    {
        var actual = discovered.ToHashSet();
        var expected = allowed.ToHashSet();
        return actual.Except(expected).Select(seam => $"Unexpected: {seam}")
            .Concat(expected.Except(actual).Select(seam => $"Stale: {seam}"))
            .Order(StringComparer.Ordinal).ToArray();
    }

    private sealed record Seam(string Path, string Type, string Property)
    {
        public override string ToString() => $"{Path}::{Type}.{Property}";
    }
}
