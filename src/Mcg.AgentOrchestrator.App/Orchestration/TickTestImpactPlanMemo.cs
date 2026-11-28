using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Stores the planner's inputs and result before any manifest, target-home, or evidence scoping.
internal sealed class TickTestImpactPlanMemo(
    Func<RepositoryChangeSummary, string, RepositoryTestImpactPlan>? planner = null,
    Func<string, IReadOnlyList<string>, RepositoryRecheckTestSelection>? selector = null)
{
    internal sealed record Candidate(string WorktreePath, string Head, string MainRevision, string MergeBase)
    {
        internal static Candidate? Read(string worktreePath, string integrationBranch, string? mergeBase = null)
        {
            var head = ConductorDriver.TryResolveGitHead(worktreePath);
            if (head is null ||
                !GitRevisionReader.Shared.TryResolve(worktreePath, $"refs/heads/{integrationBranch}", out var mainRevision))
                return null;
            if (mergeBase is null &&
                !GoalWorktrees.TryResolveCandidateMergeBase(worktreePath, integrationBranch, out mergeBase))
                return null;
            return new(worktreePath, head, mainRevision, mergeBase);
        }
    }

    private readonly record struct Key(Candidate Candidate, string Inputs);
    // Match the directory exclusions in FileSystemTestClassDeclarationReader.
    private static readonly HashSet<string> GeneratedSourceDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "artifacts", ".scratch"
    };
    private readonly Dictionary<Key, RepositoryTestImpactPlan> _plans = new();
    private readonly Dictionary<Key, RepositoryRecheckTestSelection> _reverseSelections = new();

    internal static IDisposable ActivateForTick(TickTestImpactPlanMemo memo, Action<TickTestImpactPlanMemo?> setCurrent)
    {
        setCurrent(memo);
        return new Lifetime(setCurrent);
    }

    private sealed class Lifetime(Action<TickTestImpactPlanMemo?> setCurrent) : IDisposable
    {
        private Action<TickTestImpactPlanMemo?>? _setCurrent = setCurrent;

        public void Dispose()
        {
            var clear = _setCurrent;
            _setCurrent = null;
            clear?.Invoke(null);
        }
    }

    internal T ResolveCandidateIdentity<T>(Func<T> resolve) => GoalWorktrees.WithCandidateImpactPlanner(
        (path, integrationBranch, mergeBase, summary) => Candidate.Read(path, integrationBranch, mergeBase) is { } candidate
            ? Plan(candidate, summary) : null, resolve);

    internal static (string? TreeSha, string? ManifestIdentity) ResolvePreReviewRecordedCandidateIdentity(
        TickTestImpactPlanMemo? memo, string? executionDirectory, GoalId goalId, string candidateSha,
        string integrationBranch, string? projectHomeDirectory, string? executedManifestIdentity)
    {
        (string? TreeSha, string? ManifestIdentity) Resolve() => PreReviewEvidenceReceipts.ResolveRecordedCandidateIdentity(
            executionDirectory, goalId, candidateSha, integrationBranch, projectHomeDirectory, executedManifestIdentity);

        return memo is null ? Resolve() : ManifestScopedImpactPlan.WithRawPlanner(
            (summary, root) => Candidate.Read(root, integrationBranch) is { } candidate &&
                string.Equals(candidate.Head, candidateSha, StringComparison.OrdinalIgnoreCase)
                ? memo.Plan(candidate, summary) : null, Resolve);
    }

    internal static (PreTesterGuardSelections Guards, DeveloperDeferredTestSelection Selection, bool Abandoned) CreateGuardRequest(
        TickTestImpactPlanMemo? memo, string? integrationBranch, PreTesterChangedFileGuardClasses cache,
        string worktreePath, IReadOnlyList<string> changedPaths, string mainRevision,
        DeveloperDeferredTestSelection declaration, AcceptanceGateEngineSettings settings, PreTesterEvidenceEntry? prior,
        Func<FindingEvidenceSelection, string> format, Action<PreTesterEvidenceEntry, string> record,
        PreTesterReverseDependencyObservation.Tick? resolverTick, PreTesterReverseDependencyObservation.Boundary? resolverBoundary)
    {
        if (memo is null || integrationBranch is null || Candidate.Read(worktreePath, integrationBranch, mainRevision) is not { } candidate)
            return PreTesterGuardSelections.CreateForRequest(cache, worktreePath, changedPaths, mainRevision, declaration,
                settings, prior, format, record, resolverTick, resolverBoundary);

        PreTesterGuardSelections CreateForPrior(PreTesterEvidenceEntry? entry) => ManifestScopedImpactPlan.WithRawPlanner(
            (summary, root) => string.Equals(root, worktreePath, StringComparison.Ordinal) ? memo.Plan(candidate, summary) : null,
            () => PreTesterGuardSelections.Create(cache, worktreePath, changedPaths, mainRevision, settings: settings,
                priorSelections: entry?.Outcome == "started" ? entry.Selections : null,
                reverseDependencySelector: (_, paths) => memo.SelectReverseDependents(candidate, paths),
                resolverTick: resolverTick, resolverBoundary: resolverBoundary));

        var guards = CreateForPrior(prior);
        var augmented = guards.Augment(declaration, settings, prior, format);
        var abandoned = false;
        if (prior?.Outcome == "started")
            augmented = PreTesterGuardSelections.ReconcileStarted(prior, augmented.Selections, augmented.NotRun,
                format, PreTesterStartedEvidence.DriftProgress(prior.ReceiptId), record,
                () =>
                {
                    guards = CreateForPrior(null);
                    return guards.Augment(declaration, settings, null, format);
                }, out abandoned);
        return (guards, augmented, abandoned);
    }

    internal RepositoryTestImpactPlan Plan(Candidate candidate, RepositoryChangeSummary summary)
    {
        RepositoryTestImpactPlan Compute() => (planner ?? RepositoryTestImpactPlanner.Plan)(summary, candidate.WorktreePath);
        if (!CanReuse(candidate, summary.Files.Select(file => file.Path)))
            return Compute();

        return GetOrCompute(_plans, new(candidate, JsonSerializer.Serialize(summary)), () =>
        {
            var plan = Compute();
            return plan with
            {
                Checks = Array.AsReadOnly(plan.Checks.Select(check => check with
                {
                    Command = Array.AsReadOnly(check.Command.ToArray()),
                    TestClassSelections = check.TestClassSelections is { } classes
                        ? Array.AsReadOnly(classes.ToArray()) : null,
                    ChangedTestClasses = Array.AsReadOnly(check.ChangedTestClasses.ToArray())
                }).ToArray())
            };
        });
    }

    internal RepositoryRecheckTestSelection SelectReverseDependents(Candidate candidate, IReadOnlyList<string> paths)
    {
        RepositoryRecheckTestSelection Compute() => (selector ?? RepositoryRecheckTestSelection.Read)(candidate.WorktreePath, paths);
        if (!CanReuse(candidate, paths))
            return Compute();

        return GetOrCompute(_reverseSelections, new(candidate, JsonSerializer.Serialize(paths)), () =>
        {
            var selection = Compute();
            return selection with { TestClasses = Array.AsReadOnly(selection.TestClasses.ToArray()) };
        });
    }

    private static bool CanReuse(Candidate candidate, IEnumerable<string> paths)
    {
        // Git's clean status omits ignored files. The planners also inspect project/source
        // files and the presence of changed paths on disk.
        if (GitCli.IsWorktreeDirty(candidate.WorktreePath))
            return false;
        var changedPaths = paths.Select(path => path.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ignored = GitCli.RunWithRawUtf8Output(candidate.WorktreePath,
            ["--icase-pathspecs", "ls-files", "--others", "--ignored", "--exclude-standard", "-z", "--",
                "*.cs", "*.csproj", .. changedPaths.Select(path => $":(literal){path}")]);
        return ignored.ProcessStarted && !ignored.DrainTimedOut && ignored.ExitCode == 0 &&
            !HasIgnoredPlanningInputs(candidate.WorktreePath, ignored.Output, changedPaths) &&
            string.Equals(ConductorDriver.TryResolveGitHead(candidate.WorktreePath), candidate.Head, StringComparison.Ordinal);
    }

    private static bool HasIgnoredPlanningInputs(string worktreePath, string ignoredPaths, HashSet<string> changedPaths)
    {
        if (ignoredPaths.Length == 0)
            return false;

        var inspectedDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in ignoredPaths.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (changedPaths.Contains(path) || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return true;

            var segments = path.Split('/');
            var generated = Array.FindIndex(segments, 0, segments.Length - 1, GeneratedSourceDirectories.Contains);
            if (generated < 0)
                return true;

            // Source exclusions are relative to each project. A project rooted inside an
            // excluded directory can still read these files, so retain the reuse guard.
            for (var depth = segments.Length - 1; depth > generated; depth--)
            {
                var directory = Path.Combine(worktreePath, string.Join("/", segments, 0, depth));
                try
                {
                    if (inspectedDirectories.Add(directory) && Directory.EnumerateFiles(directory, "*.csproj").Any())
                        return true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static T GetOrCompute<T>(Dictionary<Key, T> values, Key key, Func<T> compute)
    {
        lock (values)
        {
            if (!values.TryGetValue(key, out var value))
            {
                value = compute();
                values.Add(key, value);
            }
            return value;
        }
    }
}
