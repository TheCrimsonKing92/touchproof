using AcceptanceManifest = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifest;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// The initializer is a home default, not a declaration by a foreign manifest.
internal static class AcceptanceManifestOmittedChecks
{
    internal static AcceptanceManifest Scope(AcceptanceManifest manifest, string? projectHomeDirectory)
    {
        if (GoalAcceptanceVerifierHomeImpactScope.Applies(projectHomeDirectory) ||
            manifest.Checks is not { Count: 1 } ||
            !ReferenceEquals(manifest.Checks[0], AcceptanceManifestCheck.DefaultDotnetTest))
            return manifest;

        return new AcceptanceManifest
        {
            Version = manifest.Version,
            Checks = [],
            ForbiddenChangedPathGlobs = manifest.ForbiddenChangedPathGlobs,
            ToolHomes = manifest.ToolHomes
        };
    }
}
