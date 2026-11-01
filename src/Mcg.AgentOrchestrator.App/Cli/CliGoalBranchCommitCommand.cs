using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Owns a git commit and durable operator requests, never a state.db transaction.
internal static class CliGoalBranchCommitCommand
{
    internal const string Usage = "Usage: goal-branch-commit <goal-prefix> (--message <text> | --resume) [--wait[=<seconds>]] [--project=<name>]";

    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        args[0].Equals("goal-branch-commit", StringComparison.OrdinalIgnoreCase) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        IOperatorIntentStore? store = null, Func<GoalId, bool>? hasLiveAcceptanceAttempt = null)
    {
        string? newHead = null;
        string? resumeCommand = null;
        GoalBranchCommitRebindPlan? plan = null;
        var submitted = new List<string>();
        try
        {
            var options = GoalBranchCommitOptions.Parse(args);
            var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(
                args, CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli);
            var repository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
            var matches = repository.ListGoalMetadataAsync().GetAwaiter().GetResult()
                .Where(item => item.Id.StartsWith(options.GoalPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            var goalId = matches.Length switch
            {
                1 => new GoalId(matches[0].Id),
                0 => throw new InvalidOperationException("GoalNotFound: no goal matches the prefix."),
                _ => throw new InvalidOperationException("GoalAmbiguous: more than one goal matches the prefix.")
            };
            var kernel = repository.LoadGoalsAsync([goalId]).GetAwaiter().GetResult();
            var goal = kernel.GetGoal(goalId);
            if (goal.Status == GoalStatus.Completed)
                throw new InvalidOperationException("GoalCompleted: completed goals do not accept rebind or note intents.");
            var path = GoalBranchCommitPreflight.Check(goal, workspace, options.Resume, hasLiveAcceptanceAttempt);
            var previousHead = options.Resume ? GoalBranchCommitPreflight.Parent(path)
                : GoalBranchCommitPreflight.Git(path, "rev-parse", "HEAD");
            // Keep project selection explicit in recovery, even when it came from global CLI selection.
            resumeCommand = $"goal-branch-commit {goalId.Value} --resume" +
                (string.IsNullOrEmpty(workspace.ProjectName) ? "" : $" --project={workspace.ProjectName}");
            if (options.Resume)
                newHead = GoalBranchCommitPreflight.Git(path, "rev-parse", "HEAD");
            else
            {
                var commit = GitCli.RunForRepository(path, "commit", "-m", options.Message!, "-m", GoalBranchCommitPreflight.Trailer);
                // Inspect HEAD even on a Git failure: never conceal an already-created commit.
                var observedHead = GoalBranchCommitPreflight.Git(path, "rev-parse", "HEAD");
                if (!observedHead.Equals(previousHead, StringComparison.OrdinalIgnoreCase)) newHead = observedHead;
                if (newHead is not null) plan = GoalBranchCommitRebindPlan.Create(goal, previousHead, newHead);
                if (!commit.Succeeded)
                    throw new InvalidOperationException($"GitFailure (commit): {commit.Error.Trim()}");
                if (newHead is null)
                    throw new InvalidOperationException("GitFailure: commit returned success without advancing HEAD.");
                if (!GoalBranchCommitPreflight.Parent(path).Equals(previousHead, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("CandidateChanged: new commit's parent is not the preflight HEAD.");
            }
            plan = GoalBranchCommitRebindPlan.Create(goal, previousHead, newHead);
            store ??= SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            var submitter = new GoalBranchCommitIntentSubmitter(store, goalId, attribution);
            var intentIds = new List<string>();
            var noteKey = $"goal-branch-commit-note:{goalId.Value}:{newHead}";
            // First durable request protects recovery if any subsequent submission fails.
            intentIds.Add(submitter.Submit(noteKey, OperatorIntentVerbs.GoalNote, new GoalNoteOperatorIntentPayload(
                $"Commit {newHead}; rebind incomplete until intents apply. Obligations: {string.Join(", ", plan.Eligible.Select(duty => duty.Id))}. Resume: {resumeCommand}")).Id);
            foreach (var duty in plan.Eligible)
            {
                intentIds.Add(submitter.Submit(GoalBranchCommitRebindPlan.RebindKey(goalId, newHead, duty),
                    OperatorIntentVerbs.CriterionEvidenceMap, GoalBranchCommitRebindPlan.Mapping(goalId, newHead, duty)).Id);
                submitted.Add(duty.Id);
            }
            var rebound = submitted.Concat(plan.Unchanged.Where(item => item.Value == "already-bound-to-new-head")
                .Select(item => item.Key)).Order(StringComparer.Ordinal).ToArray();
            intentIds.Add(submitter.Submit(noteKey + ":submitted", OperatorIntentVerbs.GoalNote,
                new GoalNoteOperatorIntentPayload($"Commit {newHead}; rebind intents submitted for obligations: " +
                    (rebound.Length == 0 ? "none" : string.Join(", ", rebound)) + ".")).Id);
            if (options.Wait is not null)
            {
                var exitCode = CliCriterionEvidenceIntents.PrintStatus(["operator-intent-status", .. intentIds, options.Wait],
                    store, Console.Out, TimeProvider.System, TimeSpan.FromMilliseconds(250), Thread.Sleep);
                if (exitCode != 0) throw new InvalidOperationException($"IntentWaitIncomplete: status exit {exitCode}.");
            }
            var current = repository.LoadGoalsAsync([goalId]).GetAwaiter().GetResult();
            GoalBranchCommitTextView.Print(newHead, plan, submitted, current.GetPendingHumanInput(goalId));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (newHead is not null)
            {
                GoalBranchCommitTextView.Print(newHead, plan, submitted, null);
                Console.Error.WriteLine($"Resume: {resumeCommand}");
                return 2;
            }
            return 1;
        }
    }
}
