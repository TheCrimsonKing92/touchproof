using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorProviderExhaustion(string Launcher, string Source, string Diagnostic);
internal sealed record ConductorSubscriptionInvocation(int Attempt, ConductorRoundModel Model,
    WorkerProcessRunResult? Result, string? Failure, string? PrimaryReceipt,
    ConductorProviderExhaustion? PrimaryExhaustion, string? TerminalReason,
    ConductorProviderExhaustion? Exhaustion = null);

internal sealed record ConductorSubscriptionRoundResult(ConductorRoundModel Model,
    WorkerProcessRunResult Result, string Answer, string? Failure, string? TerminalReason)
{
    internal string RequireAnswer() => Failure is null ? Answer :
        throw new ConductorModelRoundException(Failure, Model.Alias, TerminalReason);
}

// Owns subscription transport and its single, exhaustion-only alternate invocation.
// Callers retain their prompt, Claude flags, timeout, receipts and durable attempt authority.
internal static class ConductorSubscriptionRoundExecutor
{
    internal static string? AuthorFailure(WorkerProcessRunResult result) =>
        result.TimedOut ? "author-timeout" : result.CaptureLimited ? "author-capture-limited" :
        result.ExitCode != 0 ? $"Author model exited {result.ExitCode}: {result.StandardError}" : null;

    internal static string? StewardFailure(WorkerProcessRunResult result) =>
        result.TimedOut ? "steward-timeout" : result.CaptureLimited ? "steward-capture-limited" :
        result.ExitCode != 0 ? $"Steward model exited {result.ExitCode}: {result.StandardError}" : null;

    internal static string? SamplerFailure(WorkerProcessRunResult result) =>
        result.TimedOut ? "plan-sampler-timeout" : result.CaptureLimited ? "plan-sampler-capture-limited" :
        result.ExitCode != 0 ? result.StandardError : null;

    internal static async Task<ConductorSubscriptionRoundResult> RunAsync(
        ModelFunctionCatalog? catalog, string purpose, string prefix, string prompt,
        string directory, TimeSpan timeout, Func<ConductorRoundModel, string> claudeCommand,
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> run,
        Func<WorkerProcessRunResult, string?> failureReason, CancellationToken cancellationToken,
        Func<string, bool>? launcherAvailable = null, int attempt = 1, int maxAttempts = 2,
        Func<bool>? reserveFallback = null, Func<ConductorSubscriptionInvocation, string?>? receipt = null)
    {
        var model = ConductorRoundModelResolver.Resolve(catalog, purpose);
        if (!model.IsValid)
        {
            receipt?.Invoke(new(attempt, model, null, model.InvalidReason, null, null, model.InvalidReason));
            throw new ConductorModelRoundException(model.InvalidReason!, null, model.InvalidReason);
        }
        launcherAvailable ??= WorkerProfileDiagnostics.LocalCommandExists;
        ConductorProviderExhaustion? primaryExhaustion = null;
        string? primaryReceipt = null;
        for (var invocation = 0; invocation < 2; invocation++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentAttempt = attempt + invocation;
            WorkerProcessRunResult result;
            try
            {
                var command = model.WorkerProfileName == "codex-cli"
                    ? string.Join(" ", ProviderCommandBuilder.Build(ProviderKind.OpenAICodexCli, model.Alias!,
                        model.ReasoningEffort, "plan", "read-only", directory))
                    : claudeCommand(model);
                result = await run(new(command, directory, timeout, prompt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                receipt?.Invoke(new(currentAttempt, model, null, $"{ex.GetType().Name}: {ex.Message}",
                    primaryReceipt, primaryExhaustion, null));
                throw;
            }
            var failure = failureReason(result);
            var answer = result.StandardOutput;
            if (failure is null && model.WorkerProfileName == "codex-cli" &&
                !CodexJsonlUsageParser.TryParseCompletedAnswer(result.StandardOutput, out answer, out var transportFailure))
                failure = transportFailure;
            var exhaustion = Exhaustion(result, model.WorkerProfileName);
            if (invocation == 0) primaryExhaustion = exhaustion;
            ConductorRoundModel? alternate = null;
            string? terminal = null;
            try
            {
                if (exhaustion is not null)
                {
                    if (invocation != 0 || currentAttempt >= maxAttempts)
                        terminal = prefix + "-attempt-budget-exhausted";
                    else
                    {
                        alternate = ConductorRoundModelResolver.Resolve(catalog, purpose, fallback: true);
                        if (!alternate.IsValid) terminal = alternate.InvalidReason;
                        else if (alternate.WorkerProfileName == model.WorkerProfileName)
                            terminal = prefix + "-fallback-same-provider";
                        else if (!launcherAvailable(alternate.WorkerProfileName == "codex-cli" ? "codex" : "claude"))
                            terminal = $"{prefix}-fallback-unavailable:{alternate.WorkerProfileName}";
                        else if (reserveFallback?.Invoke() != true)
                            terminal = prefix + "-fallback-attempt-not-reserved";
                    }
                }
            }
            catch (Exception ex)
            {
                terminal = $"{prefix}-fallback-selection-failed:{ex.GetType().Name}";
                receipt?.Invoke(new(currentAttempt, model, result, failure, primaryReceipt,
                    primaryExhaustion, terminal, exhaustion));
                throw new ConductorModelRoundException(terminal, model.Alias, terminal);
            }
            var path = receipt?.Invoke(new(currentAttempt, model, result, failure, primaryReceipt,
                primaryExhaustion, terminal, exhaustion));
            if (failure is null || exhaustion is null || terminal is not null)
                return new(model, result, answer, failure, terminal);
            primaryReceipt = path;
            model = alternate!;
        }
        throw new InvalidOperationException("Subscription invocation bound was exceeded.");
    }

    internal static ConductorProviderExhaustion? Exhaustion(WorkerProcessRunResult result, string launcher)
    {
        if (result.ExitCode == 0 || result.TimedOut || result.CaptureLimited) return null;
        if (ProviderLimitEvidenceParser.TryGetEvidenceLine(null, result.StandardError, out var diagnostic))
            return new(launcher, "stderr-diagnostic", diagnostic);
        var refusal = ProviderLimitEvidenceParser.GetCliRefusalLines(result.ExitCode,
            result.StandardOutput.Split('\n').Select(line => line.TrimEnd('\r')))
            .FirstOrDefault(ProviderLimitEvidenceParser.IsCliRefusalLimitLine);
        return refusal is null ? null : new(launcher, "stdout-refusal", refusal);
    }
}
