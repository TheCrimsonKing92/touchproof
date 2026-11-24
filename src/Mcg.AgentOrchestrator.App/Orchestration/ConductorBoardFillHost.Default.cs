using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBoardFillHost
{
    internal static ConductorBoardFillHost CreateDefault(OrchestratorWorkspace workspace,
        Func<ConductorAutonomyPolicy> policy, Func<string?>? mainHead = null,
        Func<string, CancellationToken, AuthorBriefDraftOutcome>? draft = null,
        Func<IReadOnlyList<BacklogItem>>? readBacklog = null,
        Func<IReadOnlyList<BoardFillDraftRound>, DateTimeOffset, IReadOnlyList<BacklogItem>>? schedulingBacklog = null,
        Func<DateTimeOffset>? utcNow = null, Func<string, string, IReadOnlyList<string>?>? changedFiles = null,
        Action<string, string>? appendBacklogNote = null)
    {
        changedFiles ??= CreateChangedFilesSeam(workspace);
        var snapshot = readBacklog is null ? new BoardFillBacklogSnapshotCache(workspace.BacklogStorePath) : null;
        readBacklog ??= snapshot!.Read;
        return new(
        new(ConductorBoardFillDraftStore.DefaultPath(workspace)),
        draft ?? ((id, token) => AuthorBriefDraftService.Run(id, workspace,
            new(WorkerProcessRunner.RunBufferedAsync, new GitAuthorBriefDraftRepository(workspace.ExecutionDirectory, workspace.IntegrationBranch),
                ModelFunctionCatalogStore.Load(workspace.ModelFunctionCatalogPath)),
            TextWriter.Null, TextWriter.Null, token, "board-fill-drafts", includeBoardFillMetadata: true)),
        readBacklog,
        (kernel, items) =>
        {
            var byId = items.DistinctBy(item => item.Id, StringComparer.Ordinal)
                .ToDictionary(item => item.Id, StringComparer.Ordinal);
            var dependencyClaims = new SourceBacklogClaimStore(workspace.SqliteStatePath);
            EpicPlanGoalClaims claims;
            try { claims = EpicPlanGoalClaimSnapshot.Read(workspace.SqliteStatePath, kernel.Goals.ToArray()); }
            catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                return _ => new(false, false, " [Blocked: source ownership unavailable]");
            }
            return item =>
            {
                var source = claims.Resolve(item);
                if (!source.AutomaticIntakeEligible && !source.ReselectableAfterTerminalOwners)
                    return new(false, false, " [Blocked: source already claimed or unresolved]");
                // A malformed prerequisite must block its dependent, not abort selection of independent work.
                if (item.Dependencies.Any(dependency => dependency.TargetKind == BacklogDependencyTargetKind.Backlog &&
                    byId.TryGetValue(dependency.PrerequisiteId, out var prerequisite) &&
                    claims.Resolve(prerequisite) is { OwnerGoalId: not null, Coverage: null }))
                    return new(false, false, " [Blocked: prerequisite ownership unavailable]");
                return BacklogDependencyReadiness.Evaluate(item,
                id => kernel.Goals.FirstOrDefault(goal => goal.Id.Value == id),
                id => byId.GetValueOrDefault(id),
                id =>
                {
                    try
                    {
                        var owner = dependencyClaims.ResolveClaim(kernel, id);
                        return new(kernel.Goals.FirstOrDefault(goal => goal.Id.Value == owner?.OwnerGoalId));
                    }
                    catch (LegacySourceBacklogOwnerAmbiguousException) { return new(null, true); }
                },
                goal => LandingState(workspace, goal));
            };
        }, policy, new(workspace.ConductEventsLogPath),
        items => BoardFillEpicMissingQuestion.ReadMemberships(workspace.PortfolioStorePath, items), utcNow,
        verifier: new BoardFillPremiseVerifier(
            () => ModelFunctionCatalogStore.Load(workspace.ModelFunctionCatalogPath),
            new GitAuthorBriefDraftRepository(workspace.ExecutionDirectory, workspace.IntegrationBranch), workspace.ExecutionDirectory),
        filing: CreateFilingSeams(workspace, changedFiles), mainHead: mainHead,
        trackedEdits: () => GitTrackedEditsProbe.Probe(workspace.ExecutionDirectory),
        staleMainHead: mainHead ?? (() => BoardFillStaleItemRule.ReadMainHead(workspace.ExecutionDirectory, workspace.IntegrationBranch)),
        preferredOrder: items => EpicPlanReadySliceSelector.Read(workspace, () => items),
        stalledEpics: new StalledEpicAttention(() => StalledEpicAttention.ReadCandidates(workspace, readBacklog),
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory), new(workspace.ConductEventsLogPath)),
        revise: CreateRevisionSeam(workspace),
        appendBacklogNote: appendBacklogNote ?? ((id, text) => new BacklogStore(workspace.BacklogStorePath)
            .AppendNoteAsync(id, text).GetAwaiter().GetResult()), changedFiles: changedFiles,
        epicAttention: CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory), schedulingBacklog: schedulingBacklog)
        {
            BacklogSnapshotCache = snapshot
        };
    }

    internal static Func<BoardFillVerdictRevisionRequest, CancellationToken, Task<BoardFillVerdictRevisionResult>>
        CreateRevisionSeam(OrchestratorWorkspace workspace,
            Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>>? runProcess = null,
            IAuthorBriefDraftRepository? repository = null) => async (request, token) =>
    {
        var draftRepository = repository ?? new GitAuthorBriefDraftRepository(workspace.ExecutionDirectory, workspace.IntegrationBranch);
        var observedHead = draftRepository.ResolveMainHead();
        if (observedHead != request.MainHead) return new("failed", 1, null, observedHead, [], "main-head-changed");
        var model = ConductorRoundModelResolver.Resolve(ModelFunctionCatalogStore.Load(workspace.ModelFunctionCatalogPath),
            ModelFunctionPurposes.ConductorAuthor);
        var item = AuthorBriefDraftBacklog.Read(workspace.BacklogStorePath, request.BacklogItemId);
        var measureTagged = AuthorBriefDraftMeasurementOnlyCheck.IsMeasureTagged(workspace.BacklogStorePath, item.Id);
        var costGoal = CostGoalOutcomeMetric.IsCostBacklogItem(item, workspace);
        var stack = GoalIntakeTargetStack.For(workspace);
        var lessons = new ConductorLessonSelector(workspace.OperatorLessonsStorePath)
            .Select(ConductorLessonSelector.AuthorTags("brief"));
        var prompt = AuthorBriefDraftPrompt.Render(item, lessons, request.MainHead, stack, measureTagged: measureTagged) +
            Environment.NewLine + Environment.NewLine + request.Prompt;
        var round = await AuthorBriefDraftRound.DispatchAsync(prompt, workspace.ExecutionDirectory,
            runProcess ?? WorkerProcessRunner.RunBufferedAsync, model, token).ConfigureAwait(false);
        if (round.ExitCode != 0) return new("failed", round.ExitCode, null, null, [], round.StandardError);
        var parsed = AuthorBriefDraftResultParser.Parse(round.StandardOutput);
        if (parsed is null) return new("unparseable", 0, null, null, []);
        if (parsed.Kind != "draft") return new(parsed.Kind, 0, null, null, [], parsed.Reason);

        var markdown = parsed.Markdown!;
        var checks = AuthorBriefDraftChecks.Run(markdown, request.MainHead, draftRepository, stack, measureTagged: measureTagged, costGoal: costGoal);
        var findings = BriefLint.Lint(markdown, workspace.ExecutionDirectory);
        IReadOnlyList<AuthorBriefDraftCheck> refreshed = [.. checks, .. findings
            .Where(finding => finding.Severity is BriefLintSeverity.BlocksDispatch or BriefLintSeverity.BlocksCliStart)
            .Select(finding => new AuthorBriefDraftCheck($"brief-lint:{finding.Kind}", false,
                string.IsNullOrEmpty(finding.Remedy) ? finding.Message : $"{finding.Message} remedy: {finding.Remedy}"))];
        return new("draft", 0, markdown, draftRepository.ResolveMainHead(), refreshed);
    };

    private static BoardFillFilingSeams CreateFilingSeams(OrchestratorWorkspace workspace,
        Func<string, string, IReadOnlyList<string>?> changedFiles)
    {
        var intake = new BoardFillCliGoalIntake(workspace);
        return new(intake, intake.ReadBoard, () =>
        {
            var result = GitCli.Run(workspace.ExecutionDirectory, "rev-parse", "--verify", $"{workspace.IntegrationBranch}^{{commit}}");
            return result.Succeeded ? result.Output.Trim() : null;
        }, changedFiles);
    }

    private static Func<string, string, IReadOnlyList<string>?> CreateChangedFilesSeam(OrchestratorWorkspace workspace)
    {
        // Filing and selection share successful diffs. Bound retention to the observed destination head.
        var cache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        string? cachedHead = null;
        return (from, to) =>
        {
            lock (cache)
            {
                if (cachedHead != to) { cache.Clear(); cachedHead = to; }
                if (cache.TryGetValue(from, out var cached)) return cached;
                // Include the source of a rename: a citation to that path is no longer current.
                var result = GitCli.Run(workspace.ExecutionDirectory, "diff", "--no-renames", "--name-only", from, to);
                if (!result.Succeeded || result.DrainTimedOut) return null;
                var paths = result.Output.Split('\n').Select(line => line.TrimEnd('\r'))
                    .Where(line => line.Length > 0).ToArray();
                // Git quotes paths it cannot print verbatim; do not compare an encoded path as if literal.
                if (paths.Any(path => path.StartsWith('"'))) return null;
                cache[from] = paths;
                return paths;
            }
        };
    }

    private static string LandingState(OrchestratorWorkspace workspace, Goal goal)
    {
        var journal = GoalOperationJournal.Read(workspace.ExecutionDirectory, goal.Id);
        return GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(
            GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id) is not null,
            IsMerged: GoalOperationJournal.HasCompletedLandingEvidence(journal),
            IsRecorded: journal.LatestByOperation.Any(entry =>
                entry.Operation.Equals("conductor:record", StringComparison.OrdinalIgnoreCase) &&
                entry.Status == GoalOperationStatus.Completed),
            IsCleanedUp: journal.LatestByOperation.Any(entry =>
                entry.Operation.Equals("workspace:remove", StringComparison.OrdinalIgnoreCase) &&
                entry.Status == GoalOperationStatus.Completed))).ToString();
    }
}
