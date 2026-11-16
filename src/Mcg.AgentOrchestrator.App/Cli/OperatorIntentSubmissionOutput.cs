using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using ConductorLiveness = Mcg.AgentOrchestrator.App.OwnerConsole.ConductorLiveness;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class OperatorIntentSubmissionOutput
{
    internal static void PrintGraphRepair(OperatorIntentRecord persisted, string directory, bool json) =>
        Print(persisted, directory, json,
            $"Operator intent queued: id={persisted.Id} verb={persisted.Verb} goal={persisted.GoalId} status={persisted.Status} replay_key={persisted.IdempotencyKey}; poll with operator-intent-status {persisted.Id} (or add --wait).",
            $"Outcome: {persisted.Outcome ?? "pending"}. Bare replay returns this operation across apply/restart. Use --idempotency-key <fresh-key> for a distinct later identical insertion.");
    internal static IReadOnlyList<string> PrepareOperatorIntentArguments(
        IReadOnlyList<string> args, out bool emitAgentReadEnvelope)
    {
        emitAgentReadEnvelope = false;
        return CliPersistentStateRunner.IsInboxBackedGoalScopedTaskMutationCommand(args)
            ? StripJsonFlag(args, out emitAgentReadEnvelope)
            : args;
    }

    internal static IReadOnlyList<string> StripJsonFlag(
        IReadOnlyList<string> args, out bool emitAgentReadEnvelope)
    {
        emitAgentReadEnvelope = args.Any(arg => arg.Equals("--json", StringComparison.OrdinalIgnoreCase));
        return emitAgentReadEnvelope
            ? args.Where(arg => !arg.Equals("--json", StringComparison.OrdinalIgnoreCase)).ToArray()
            : args;
    }

    internal static void PrintTask(OperatorIntentRecord persisted, string orchestratorDirectory,
        string? suppliedGoalSelector, string goalId, string taskId, bool emitAgentReadEnvelope) =>
        Print(persisted, orchestratorDirectory, emitAgentReadEnvelope,
            $"Operator intent queued: id={persisted.Id} verb={persisted.Verb} " +
            $"selector={suppliedGoalSelector ?? "current"} goal={goalId} " +
            $"task={taskId} status={persisted.Status}; poll with operator-intent-status {persisted.Id} (or add --wait).");

    internal static void PrintAnswer(OperatorIntentRecord persisted, string orchestratorDirectory,
        string goalId, bool emitAgentReadEnvelope) =>
        Print(persisted, orchestratorDirectory, emitAgentReadEnvelope,
            $"Operator intent queued: id={persisted.Id} verb={persisted.Verb} " +
            $"goal={goalId} status={persisted.Status}; poll with operator-intent-status {persisted.Id} (or add --wait).");

    internal static void PrintCancelDispatch(OperatorIntentRecord persisted, string orchestratorDirectory,
        string goalId, string taskId, string nextStepLine, bool emitAgentReadEnvelope) =>
        Print(persisted, orchestratorDirectory, emitAgentReadEnvelope,
            $"Operator intent queued: id={persisted.Id} verb={persisted.Verb} " +
            $"goal={goalId} task={taskId} status={persisted.Status}; " +
            $"poll with operator-intent-status {persisted.Id} (or add --wait).", nextStepLine);

    internal static void PrintWorkspace(OperatorIntentRecord persisted, string orchestratorDirectory,
        bool emitAgentReadEnvelope, TextWriter? output = null) =>
        Print(persisted, orchestratorDirectory, emitAgentReadEnvelope,
            $"Operator intent queued: id={persisted.Id} verb={persisted.Verb} scope=workspace status={persisted.Status}; poll with operator-intent-status {persisted.Id} (or add --wait).",
            output: output);

    internal static void PrintCriterionEvidence(OperatorIntentRecord persisted, string orchestratorDirectory,
        string goalId, string? targetLine, bool emitAgentReadEnvelope, IConductLockPidProbe? pidProbe = null) =>
        Print(persisted, orchestratorDirectory, emitAgentReadEnvelope,
            CriterionEvidenceQueuedLine(persisted, goalId), targetLine, pidProbe: pidProbe);

    internal static void PrintCriterionEvidenceBatch(IReadOnlyList<OperatorIntentRecord> persisted,
        string orchestratorDirectory, string goalId, IReadOnlyList<string> targetLines, bool emitAgentReadEnvelope) =>
        Print(orchestratorDirectory, emitAgentReadEnvelope,
            emitAgentReadEnvelope ? "[" + string.Join(",", persisted.Select(intent => OperatorIntentAgentReadView.Render(intent))) + "]" : null,
            persisted.Select(intent => CriterionEvidenceQueuedLine(intent, goalId)).ToArray(), targetLines);

    private static string CriterionEvidenceQueuedLine(OperatorIntentRecord persisted, string goalId) =>
        $"Operator intent queued: id={persisted.Id} verb={persisted.Verb} goal={goalId} " +
        $"status={persisted.Status}; poll with operator-intent-status {persisted.Id} (or add --wait).";

    private static void Print(OperatorIntentRecord persisted, string orchestratorDirectory,
        bool emitAgentReadEnvelope, string queuedLine, string? followUpLine = null, TextWriter? output = null,
        IConductLockPidProbe? pidProbe = null) =>
        Print(orchestratorDirectory, emitAgentReadEnvelope,
            emitAgentReadEnvelope ? OperatorIntentAgentReadView.Render(persisted) : null,
            [queuedLine], followUpLine is null ? [] : [followUpLine], output, pidProbe);

    private static void Print(string orchestratorDirectory, bool emitAgentReadEnvelope,
        string? renderedDocument, IReadOnlyList<string> queuedLines, IReadOnlyList<string> followUpLines,
        TextWriter? output = null, IConductLockPidProbe? pidProbe = null)
    {
        var writer = output ?? Console.Out;
        if (emitAgentReadEnvelope)
            writer.WriteLine(renderedDocument);
        else
            foreach (var queuedLine in queuedLines)
                writer.WriteLine(queuedLine);
        foreach (var followUpLine in followUpLines)
            (emitAgentReadEnvelope ? Console.Error : writer).WriteLine(followUpLine);
        PrintLivenessWarning(orchestratorDirectory, emitAgentReadEnvelope ? Console.Error : writer, pidProbe);
    }

    internal static void PrintLivenessWarning(string orchestratorDirectory, TextWriter writer,
        IConductLockPidProbe? pidProbe = null)
    {
        switch (ConductorLoopLease.ProbeLiveness(orchestratorDirectory, pidProbe))
        {
            case ConductorLiveness.NotRunning:
                writer.WriteLine(ConductorLoopLease.InactiveWarning);
                break;
            case ConductorLiveness.Unknown:
                writer.WriteLine(ConductorLoopLease.UnknownWarning);
                break;
        }
    }
}
