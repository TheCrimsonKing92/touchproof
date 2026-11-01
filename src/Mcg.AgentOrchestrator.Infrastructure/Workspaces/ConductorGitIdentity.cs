namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns identities written by the conductor and the historical pairs accepted as its commits.
internal static class ConductorGitIdentity
{
    internal const string ConductorName = "touchproof";
    internal const string ConductorEmail = "touchproof@localhost";
    internal const string GateName = "Touchproof Acceptance Gate";
    internal const string GateEmail = "acceptance-gate@localhost";
    internal const string CanaryName = "Touchproof Canary";
    internal const string CanaryEmail = "canary@localhost";

    internal static IReadOnlyList<string> ConductorArguments { get; } = Array.AsReadOnly<string>(
        ["-c", $"user.name={ConductorName}", "-c", $"user.email={ConductorEmail}"]);

    internal static IReadOnlyList<string> GateArguments { get; } = Array.AsReadOnly<string>(
        ["-c", $"user.name={GateName}", "-c", $"user.email={GateEmail}"]);

    internal static IReadOnlyList<string> CanaryArguments { get; } = Array.AsReadOnly<string>(
        ["-c", $"user.name={CanaryName}", "-c", $"user.email={CanaryEmail}"]);

    internal static bool IsConductorCommitter(string? name, string? email) => (name, email) is
        (ConductorName, ConductorEmail) or
        (GateName, GateEmail) or
        ("mcg-orchestrator", "mcg-orchestrator@localhost") or
        ("MCG Acceptance Gate", "acceptance-gate@localhost");
}
