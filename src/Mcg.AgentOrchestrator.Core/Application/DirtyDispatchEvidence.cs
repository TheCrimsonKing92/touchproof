namespace Mcg.AgentOrchestrator.Core;

public static class DirtyDispatchEvidence
{
    public static bool TryBuildDirtyDispatchRecovery(TaskSpec task, out DirtyDispatchRecovery recovery)
    {
        recovery = DirtyDispatchRecovery.None;
        if (task.RequiredRole is not (AgentRole.Developer or AgentRole.Tester) ||
            task.LastVerification is not { Succeeded: false } verification ||
            !IsDirtyDispatchGuardFailure(verification))
        {
            return false;
        }

        var changedFiles = ExtractChangedFiles(verification.StandardError);
        var verificationEvidence = ExtractVerificationEvidence(verification.StandardOutput, verification.StandardError);
        var label = verificationEvidence.Length > 0 ? "dirty-useful" : "dirty-unverified";
        recovery = new DirtyDispatchRecovery(
            label,
            changedFiles,
            verificationEvidence,
            verification.WorkingDirectory);
        return true;
    }

    internal static bool IsDirtyDispatchGuardFailure(TaskVerificationRecord verification)
    {
        return verification.StandardError.Contains(
            "Developer/Tester dispatch exited 0 but left the worktree dirty",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string[] ExtractChangedFiles(string standardError)
    {
        foreach (var rawLine in standardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var marker = "status_short=";
            var markerIndex = rawLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                continue;
            }

            var value = rawLine[(markerIndex + marker.Length)..].Trim();
            if (value.EndsWith(".", StringComparison.Ordinal))
            {
                value = value[..^1];
            }

            var entries = value
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(entry => !string.Equals(entry, "clean", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entry, "unavailable", StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .ToArray();
            return entries.Length == 0 ? ["unavailable"] : entries;
        }

        return ["unavailable"];
    }

    private static string[] ExtractVerificationEvidence(string standardOutput, string standardError)
    {
        var lines = $"{standardOutput}\n{standardError}"
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Contains("Developer/Tester dispatch exited 0 but left the worktree dirty", StringComparison.OrdinalIgnoreCase))
            .Where(line => DispatchCompletionEvidence.HasVerificationEvidence(line, string.Empty))
            .Take(3)
            .ToArray();

        return lines;
    }
}
