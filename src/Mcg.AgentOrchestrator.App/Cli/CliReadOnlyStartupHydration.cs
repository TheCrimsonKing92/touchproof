using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliReadOnlyStartupHydration
{
    internal static async Task<(Goal? CurrentGoal, bool Hydrated)> PrepareStartupAsync(
        IReadOnlyList<string> args,
        IOrchestratorStateRepository stateRepository)
    {
        if (CliKeyedGoalCreationSubmission.IsCommand(args) || CliReadOnlyCommandRunner.IsReadOnlyCommand(args))
            return (null, false);

        var kernel = await stateRepository.LoadAsync();
        return (OrchestratorEntityResolver.GetLatestGoal(kernel), true);
    }

    internal static bool ExecuteStartupCommand(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        bool hydrated,
        WorktreeCleanupContext? acceptanceCleanupContext = null,
        Action? onReadOnlyDeclined = null,
        ConductorLoopLeaseController? startupLoopLease = null)
    {
        if (!hydrated && !CliKeyedGoalCreationSubmission.IsCommand(args))
        {
            if (CliReadOnlyCommandRunner.TryExecute(args, stateRepository, workspace, providers, ref agents, ref workerProfiles, ref currentGoal, out var changed))
                return changed;

            onReadOnlyDeclined?.Invoke();
            currentGoal = OrchestratorEntityResolver.GetLatestGoal(
                stateRepository.LoadAsync().GetAwaiter().GetResult());
        }

        return CliPersistentStateRunner.ExecuteCommand(args, stateRepository, workspace, ref agents,
            providers, ref workerProfiles, ref currentGoal,
            acceptanceCleanupContext: acceptanceCleanupContext,
            skipReadOnlyRoute: !hydrated, startupLoopLease: startupLoopLease);
    }
}

internal static class CliKeyedGoalCreationSubmission
{
    internal static bool IsCommand(IReadOnlyList<string> args) =>
        CliPersistentStateRunner.IsGoalCreateCommand(args) && CliPersistentStateRunner.HasRequestKey(args) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static bool Submit(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents,
        ref Goal? currentGoal,
        CliPersistentStateRunner.OperatorIntentSubmissionSource submissionSource,
        bool allowTerminalSourceClaimTakeover)
    {
        var request = GoalIntakeRequestMapper.Map(args, workspace, agents)!;
        var arguments = PrepareGoalCreationArguments(args, request, out var briefFileValueIndex);
        var summary = GoalCreationSummaryView.IsRequested(args);
        var requests = new GoalIntakeRequestStore(workspace.SqliteStatePath);
        var reservation = request.Reserve(requests);
        if (reservation.Kind == GoalIntakeReservationKind.Replay)
        {
            PrintKeyedGoalCreationReplay(reservation.Record, request, repository, workspace, summary, ref currentGoal);
            return false;
        }

        var payload = new GoalCreationOperatorIntentPayload(request.RequestKey, request.Fingerprint, request.Mode,
            request.Objective, request.RequestedEpicId, arguments, briefFileValueIndex,
            agents.ToArray(), allowTerminalSourceClaimTakeover);
        var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(args, submissionSource);
        var intent = new OperatorIntentRecord(Guid.NewGuid().ToString("N"), request.RequestKey,
            OperatorIntentVerbs.GoalCreate, OperatorIntentScopes.Workspace, null,
            JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], attribution.Actor, attribution.Channel,
            attribution.AuthenticationAssurance, DateTimeOffset.UtcNow, ActorKind: attribution.ActorKind);
        SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .EnqueueAsync(intent).GetAwaiter().GetResult();

        GoalIntakeReceiptTextView.PrintGoalIntakeReceipt(reservation.Record);
        if (summary) GoalCreationSummaryView.PrintProgress(reservation.Record);
        Console.WriteLine("Queued goal creation. To apply without a running conductor: conductor apply-intents --workspace");
        return false;
    }

    private static IReadOnlyList<string> PrepareGoalCreationArguments(
        IReadOnlyList<string> args, GoalIntakeRequestDescriptor request, out int? briefFileValueIndex)
    {
        static bool IsBriefFileOption(string value) =>
            value.Split('=', 2)[0].Equals("--brief-file", StringComparison.OrdinalIgnoreCase) ||
            value.Split('=', 2)[0].Equals("--text-file", StringComparison.OrdinalIgnoreCase);

        briefFileValueIndex = null;
        var arguments = (request.RequestedEpicId is { } epicId
            ? EpicAtCreation.UseResolvedArgument(args, epicId) : args).ToList();
        var index = arguments.FindIndex(1, IsBriefFileOption);
        if (index < 0) return arguments;
        if (!arguments[1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Provide either inline text or a goal brief file, not both.");
        if (arguments.Skip(index + 1).Any(IsBriefFileOption))
            throw new ArgumentException("Provide only one goal brief file option.");
        // Keep the original source option for draft checks. The payload carries the contents
        // and the conductor supplies the path when it runs the existing creation parser.
        var separator = arguments[index].IndexOf('=');
        if (separator >= 0)
        {
            arguments[index] = arguments[index][..separator];
            arguments.Insert(index + 1, "");
        }
        else
            arguments[index + 1] = "";
        briefFileValueIndex = index + 1;
        return arguments;
    }

    private static void PrintKeyedGoalCreationReplay(
        GoalIntakeRequestRecord record,
        GoalIntakeRequestDescriptor request,
        ITransactionalOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace,
        bool summary,
        ref Goal? currentGoal)
    {
        if (!summary) GoalIntakeReceiptTextView.PrintGoalIntakeReceipt(record);
        if (record.State != GoalIntakeRequestStates.Created)
        {
            if (summary) GoalCreationSummaryView.PrintProgress(record);
            if (record.State == GoalIntakeRequestStates.Failed)
                throw new InvalidOperationException(
                    $"GOAL_INTAKE_REPLAY_FAILED requestKey={request.RequestKey} " +
                    $"failureCode={record.FailureCode} detail={record.FailureDetail}");
            return;
        }

        var snapshot = record.GoalId is { } id
            ? repository.LoadGoalAsync(new GoalId(id)).GetAwaiter().GetResult()
            : null;
        if (snapshot is null)
            throw new InvalidOperationException(
                $"GOAL_INTAKE_RECEIPT_INVALID requestKey={request.RequestKey} reason=created-goal-missing");
        currentGoal = Goal.FromSnapshot(snapshot);
        if (request.RequestedEpicId is { } epicId)
        {
            var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
            if (portfolio.GetGoalMembershipAsync(snapshot.Id).GetAwaiter().GetResult() is null)
                EpicAtCreation.AssignAfterCommit(workspace, PortfolioMemberKind.Goal, snapshot.Id, epicId);
        }
        if (summary)
            GoalCreationSummaryView.Print(currentGoal, "replayed", workspace.PortfolioStorePath);
        else
            GoalTextView.PrintGoal(currentGoal);
    }

    internal static string ResolveGoalIntakeFailureCode(Exception exception)
    {
        var firstToken = exception.Message
            .Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (firstToken is not null &&
            firstToken.Length <= 100 &&
            firstToken.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character == '_'))
        {
            return firstToken;
        }

        return exception is ArgumentException
            ? "GOAL_INTAKE_ARGUMENT_INVALID"
            : "GOAL_INTAKE_CREATE_FAILED";
    }
}
