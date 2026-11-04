using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GateTestSelectorDiagnosticPreparation
{
    internal static IReadOnlyList<string> Build(Goal goal, string? candidate, string worktree,
        string orchestratorDirectory, IEnumerable<HumanInputRequest> answers, string? projectHome = null)
    {
        if (!goal.Tasks.Any(task => task.RequiredRole == AgentRole.Developer && task.Status == WorkTaskStatus.Completed) ||
            !goal.GateTestObligations.Any(o => o.BriefVersion == goal.AuthoritativeBrief.Version)) return [];
        var answered = answers.ToArray();
        var report = ReadReport(goal, candidate);
        var evidence = new List<GateTestSelectorDiagnostic.Evidence>();
        try
        {
            var manifestPath = AcceptanceManifestLocator.Resolve(worktree, projectHome);
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var checks = manifest.RootElement.GetProperty("checks")
                .Deserialize<GoalAcceptanceVerifier.AcceptanceManifestCheck[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            var clean = GitCli.Run(worktree, "status", "--porcelain", "--untracked-files=no");
            var head = GitCli.Run(worktree, "rev-parse", "HEAD");
            var bound = clean.ExitCode == 0 && string.IsNullOrWhiteSpace(clean.Output) && head.ExitCode == 0 &&
                string.Equals(head.Output.Trim(), candidate, StringComparison.OrdinalIgnoreCase);
            if (bound)
            {
                var reader = new FileSystemTestClassDeclarationReader(worktree);
                var projects = checks.Where(check => check.Project?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(check => check.Project!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var rows = new List<ProspectiveEvidenceTrxRows.Row>();
                string? unavailable = projects.Length == 0 ? "no scoped CSharp declaration projects" : null;
                unavailable ??= UntrackedDeclarationInputs(worktree, projects);
                foreach (var project in projects)
                {
                    if (unavailable is not null) break;
                    var declarations = reader.ReadProject(Path.GetDirectoryName(project) ?? ".");
                    if (declarations.Outcome is TestClassDeclarationOutcome.Unavailable or TestClassDeclarationOutcome.Unreadable)
                        unavailable = $"{project}: {declarations.Outcome}";
                    rows.AddRange(declarations.ClassNames.Select(name =>
                        new ProspectiveEvidenceTrxRows.Row(name, name, name, null, project)));
                }
                var afterHead = GitCli.Run(worktree, "rev-parse", "HEAD");
                var afterStatus = GitCli.Run(worktree, "status", "--porcelain", "--untracked-files=no");
                var changed = afterHead.ExitCode != 0 || afterStatus.ExitCode != 0 || afterHead.Output != head.Output ||
                    !string.IsNullOrWhiteSpace(afterStatus.Output);
                if (changed) unavailable = "candidate changed during declaration read";
                unavailable ??= UntrackedDeclarationInputs(worktree, projects);
                // Class declarations cannot establish absence of a method or a non-CSharp identity.
                evidence.Add(new(string.Join(", ", projects), "declared CSharp classes (class granularity only)",
                    candidate, goal.AuthoritativeBrief.Version, unavailable, rows, [], Complete: false));
                // Command declarations remain usable without CSharp projects or readable CSharp sources.
                evidence.Add(new(manifestPath, "declared manifest commands", candidate, goal.AuthoritativeBrief.Version,
                    changed ? "candidate changed during declaration read" : null, [], [],
                    new(checks, new Dictionary<string, bool>(), false), Complete: false));
            }
            else evidence.Add(new(manifestPath, "declarations", candidate, goal.AuthoritativeBrief.Version,
                "candidate worktree binding unavailable or dirty", [], []));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            evidence.Add(new("acceptance manifest/declarations", "declarations", candidate, goal.AuthoritativeBrief.Version,
                "missing or unreadable scoped declarations: " + ex.GetType().Name, [], []));
        }
        var sources = ProspectiveEvidenceGateResolver.ReadAttempts(orchestratorDirectory, goal.Id, candidate)
            .Select(attempt => new GateTestObligationEvaluator.Source(attempt.Id, attempt.CompletedAt,
                attempt.Paths, attempt.BriefVersion, Checks: attempt.Checks, WorktreeRoot: attempt.WorktreeRoot))
            .Concat(GroupedGateReceiptTrxEvidence.ReadPassedReceipts(orchestratorDirectory, goal.Id, candidate)
                .Select(receipt => new GateTestObligationEvaluator.Source(receipt.ReceiptId, receipt.CompletedAt,
                    receipt.TrxPaths, receipt.BriefVersion))).ToArray();
        foreach (var source in sources) evidence.Add(ReadReceipt(source, orchestratorDirectory, candidate));
        var evaluated = GateTestObligationEvaluator.Evaluate(goal, orchestratorDirectory, candidate, answeredRequests: answered);
        return GateTestSelectorDiagnostic.Render(GateTestSelectorDiagnostic.Project(goal, candidate, evidence, report, answered, evaluated));
    }

    internal static GateTestSelectorDiagnostic.Evidence ReadReceipt(GateTestObligationEvaluator.Source source,
        string directory, string? candidate)
    {
        try
        {
            if (source.Paths.Count == 0 || source.Paths.Any(path => !File.Exists(Path.GetFullPath(path, directory))))
                return new(source.Id, "executed receipt", candidate, source.BriefVersion, "missing identity report", [], []);
            var rows = source.Paths.SelectMany(path => ProspectiveEvidenceTrxRows.Read(Path.GetFullPath(path, directory))).ToArray();
            var declared = DeclaredTestReportRows.Read(source.Paths, directory, source.Checks, source.WorktreeRoot);
            return new(source.Id + ": " + string.Join(", ", source.Paths), "executed receipt", candidate, source.BriefVersion,
                declared.UnavailableReason ?? ((declared.Applies ? declared.Rows.Count == 0 : rows.Length == 0)
                    ? "missing/empty applicable identity report" : null),
                declared.Applies ? [] : rows, declared.Rows, source.Checks, EvaluatorSourceId: source.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new(source.Id, "executed receipt", candidate, source.BriefVersion, "unreadable identity report: " + ex.GetType().Name, [], []);
        }
    }

    private static string? UntrackedDeclarationInputs(string worktree, IReadOnlyList<string> projects)
    {
        if (projects.Count == 0) return null;
        // Include ignored inputs too: the filesystem reader observes them just like untracked files.
        // Match its generated-directory exclusions without introducing another filesystem traversal.
        var paths = projects.Select(project => (Path.GetDirectoryName(project) ?? ".").Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = GitCli.Run(worktree, ["ls-files", "--others", "-z", "--", .. paths,
            ":(exclude,glob)**/bin/**", ":(exclude,glob)**/obj/**",
            ":(exclude,glob)**/artifacts/**", ":(exclude,glob)**/.scratch/**"]);
        if (result.ExitCode != 0) return "candidate declaration tracking unavailable";
        return result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(path =>
            path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            ? "untracked candidate declaration inputs" : null;
    }

    private static GateTestSelectorDiagnostic.Report? ReadReport(Goal goal, string? candidate)
    {
        var reports = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Developer && task.Status == WorkTaskStatus.Completed)
            .Select(task => (Task: task, Verification: task.VerificationHistory.LastOrDefault()))
            .Where(item => item.Verification is { Succeeded: true })
            .OrderByDescending(item => item.Verification!.CompletedAt).ToArray();
        var selected = reports.FirstOrDefault(item => !string.IsNullOrWhiteSpace(candidate) &&
            string.Equals(ReviewerEvidenceBriefSection.ResolveVerifiedCommit(item.Task, item.Verification!),
                candidate, StringComparison.OrdinalIgnoreCase));
        if (selected.Verification is null) selected = reports.FirstOrDefault();
        if (selected.Verification is not { } verification) return null;
        var commit = ReviewerEvidenceBriefSection.ResolveVerifiedCommit(selected.Task, verification);
        var current = goal.RefinedSpec is { } spec
            ? EffectiveAcceptanceCriteriaVersion.IsCapturedHashCurrent(spec, goal.EffectiveAcceptanceCriteriaCorrections,
                verification.AcceptanceCriteriaVersionHash)
            : verification.AcceptanceCriteriaVersionHash == EffectiveAcceptanceCriteriaVersion.ComputeForGoal(goal);
        var dispatch = SubscriptionLimitEvidence.FindDispatchForVerification(selected.Task, verification);
        current &= dispatch?.BriefVersion == goal.AuthoritativeBrief.Version;
        WorkerResultBlockers.TryFindTests(verification, out var tests);
        return new($"Developer task={selected.Task.Id.Value}; completed={verification.CompletedAt:O}; commit={commit}; " +
            $"criteria_hash={verification.AcceptanceCriteriaVersionHash ?? "unknown"}; dispatch_brief={dispatch?.BriefVersion.ToString() ?? "unknown"}; " +
            $"current_brief={goal.AuthoritativeBrief.Version}", commit, current,
            DeclaredTestEvidenceSelectors.FromDeveloperReport(tests));
    }
}
