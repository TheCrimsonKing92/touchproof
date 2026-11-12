using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class AttributedFailureDiagnosisInquiry(InquiryDispatcher dispatcher, IReadOnlyList<AgentDefinition> agents,
    string promptDirectory, string receiptDirectory, string eventsDirectory)
{
    internal async Task<AttributedFailureDiagnosisOutcome> RunAsync(Goal goal, AttributedFailureRouteDecision route,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var agent = ResolveAgent(goal);
        if (agent is null)
            return AttributedFailureDiagnosisOutcome.Unavailable(route, "failed", "Reviewer inquiry agent unavailable");
        var task = goal.Tasks.Single(task => task.Id == route.DeveloperTaskId);
        var result = await dispatcher.DispatchAsync(new InquiryDispatchRequest(goal, task,
            AttributedFailureDiagnosisPrompt.Build(goal, route.Failure!), promptDirectory, receiptDirectory, eventsDirectory,
            AgentSelection: agent, Timeout: timeout), cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
            return AttributedFailureDiagnosisOutcome.Unavailable(route, "timed-out", "inquiry timed out after 120 seconds");
        if (result.Receipt.ExitCode != 0)
            return AttributedFailureDiagnosisOutcome.Unavailable(route, "failed", "inquiry failed with exit code " + result.Receipt.ExitCode);
        return AttributedFailureDiagnosisPrompt.Parse(route, result.StandardOutput);
    }

    internal InquiryAgentSelection? ResolveAgent(Goal goal)
    {
        var dispatch = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Reviewer)
            .SelectMany(task => task.DispatchHistory).OrderByDescending(item => item.DispatchedAt).FirstOrDefault();
        if (dispatch is not null)
        {
            var kind = dispatch.WorkerProviderKind == ProviderKind.Unknown ? ResolveProfile(dispatch.WorkerName) : dispatch.WorkerProviderKind;
            if (Supported(kind)) return new(kind, dispatch.ModelName, dispatch.ReasoningEffort, dispatch.WorkerName);
        }
        var reviewer = agents.FirstOrDefault(agent => agent.Role == AgentRole.Reviewer && agent.Subscription is not null);
        if (reviewer?.Subscription is not { } subscription) return null;
        var provider = ResolveProfile(subscription.WorkerProfileName);
        return Supported(provider)
            ? new(provider, subscription.ModelAlias ?? reviewer.Model.ModelName,
                subscription.ReasoningEffort ?? reviewer.Model.ReasoningEffort, subscription.WorkerProfileName)
            : null;
    }

    private static bool Supported(ProviderKind provider) => provider is
        ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexLuna or ProviderKind.AnthropicClaudeCli;

    private static ProviderKind ResolveProfile(string profile)
    {
        try { return WorkerProviderCatalog.Default().ResolveProfile(profile).Identity.Kind; }
        catch (InvalidOperationException) { return ProviderKind.Unknown; }
        catch (KeyNotFoundException) { return ProviderKind.Unknown; }
    }
}
