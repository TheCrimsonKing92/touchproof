using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class RevertPushPolicyGate
{
    internal enum Outcome { Pushed, Held, Skipped }

    internal readonly record struct Result(Outcome Status, string? Reason = null);

    internal static Result Push(string orchestratorDirectory, string? root, string revertCommit, string? branch,
        Action<string> appendConductEvent, Func<string, string, string, bool>? push = null)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(branch))
            return new(Outcome.Held, "no-push-target");

        ConductorAutonomyPolicy policy;
        try
        {
            policy = ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(new DirectoryInfo(orchestratorDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or NotSupportedException)
        {
            return new(Outcome.Held, "policy-unreadable");
        }

        if (!policy.AdoptedBuildPushEnabled)
            return new(Outcome.Skipped, "adopted-build-push-disabled");

        try
        {
            var pushed = push is not null
                ? push(root, revertCommit, branch)
                : AdoptedBuildPushService.CreateDefault(root, appendConductEvent).Push(revertCommit, branch: branch);
            return pushed ? new(Outcome.Pushed) : new(Outcome.Held, "push-failed");
        }
        catch (Exception ex)
        {
            // Publication is best effort after the local revert, including a cancelled push.
            return new(Outcome.Held, ex.GetType().Name);
        }
    }
}
