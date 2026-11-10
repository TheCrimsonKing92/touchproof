using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PanelJudgeInvocation
{
    internal static async Task<PanelJudgeResult> RunAsync(string judge, PanelJudgeBinding binding,
        Func<string, string> command, Func<string, string, (string Answer, string? Usage, bool ProviderFault)> decode,
        Func<WorkerProcessRunRequest, CancellationToken, Task<PanelProcessResult>> process,
        PanelCase item, CancellationToken cancellationToken)
    {
        if (binding.Error is { } invalid)
            return new(judge, PanelJudgeOutcome.Skipped, null, "", "", Reason: invalid, WorkerProfileName: binding.WorkerProfileName);
        var directory = Path.Combine(OrchestratorTempRoot.GetRoot(), "mcg-panel-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var result = await process(new(command(directory), directory,
                ConductorJudgePanelBudgets.JudgeTimeout, PanelV0Contract.Prompt(item)), cancellationToken).ConfigureAwait(false);
            var decoded = decode(result.Stdout, result.Stderr);
            var outcome = result.TimedOut ? PanelJudgeOutcome.TimedOut :
                result.Failure is not null || result.ExitCode != 0 || decoded.ProviderFault ? PanelJudgeOutcome.InvocationFailed :
                PanelV0Contract.Validate(decoded.Answer, item.Id);
            return new(judge, outcome, result.ExitCode, result.Stdout, result.Stderr, decoded.Answer,
                PanelJudgeAvailability.ObservedReason(result, decoded.ProviderFault, outcome), decoded.Usage, binding.Alias, binding.WorkerProfileName);
        }
        catch (OperationCanceledException)
        { return new(judge, PanelJudgeOutcome.TimedOut, null, "", "", Reason: "cancelled", ModelAlias: binding.Alias, WorkerProfileName: binding.WorkerProfileName); }
        catch (Exception exception)
        { return new(judge, PanelJudgeOutcome.InvocationFailed, null, "", "", Reason: exception.Message, ModelAlias: binding.Alias, WorkerProfileName: binding.WorkerProfileName); }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
