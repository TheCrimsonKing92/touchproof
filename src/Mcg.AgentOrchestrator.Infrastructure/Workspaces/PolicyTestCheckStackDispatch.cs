using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class PolicyTestCheckStackDispatch
{
    // This is the one place a policy check's stack is chosen.
    internal static AcceptanceManifestCheck? ToManifestCheck(
        string worktreePath,
        VerificationPolicyCheck check)
    {
        var kind = ManifestTestCheckKinds.Canonicalize(check.Kind);
        if (kind == ManifestTestCheckKinds.Dotnet)
        {
            return DotnetTestCheckTranslator.TryFromCommandLine(worktreePath, check.Name, check.CommandLine);
        }

        if (kind == ManifestTestCheckKinds.Crosswalk &&
            ManifestTestPolicyCheck.TryReadCrosswalkCommand(check, out var project, out var arguments))
        {
            return new AcceptanceManifestCheck
            {
                Name = check.Name, Type = kind, Project = project, Arguments = arguments
            };
        }

        return null;
    }
}
