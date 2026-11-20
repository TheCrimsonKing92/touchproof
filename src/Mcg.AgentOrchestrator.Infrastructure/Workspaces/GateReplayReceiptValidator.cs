using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum GateReplayDiagnostic
{
    Ready,
    MissingReceipt,
    InvalidReceipt,
    CandidateShaMismatch,
    ObservedMainShaMismatch,
    ExecutablePathMismatch,
    ExecutableHashMismatch,
    MissingArtifact,
    ArtifactPathMismatch,
    MissingRequiredEnvironment,
    MissingWorkingDirectory,
    InvalidEnvironment,
    AcceptanceLeaseRequired
}

public sealed record GateReplayArtifactFact(GateReplayArtifactRole Role, string Path, bool Exists);

public sealed record GateReplayFacts(
    string? CandidateSha,
    string? ObservedMainSha,
    string? ExecutablePath,
    string? ExecutableSha256,
    IReadOnlyList<GateReplayArtifactFact> Artifacts);

public enum GateReplayRegateRoute
{
    OperatorAdjudicateReopenRegate
}

public sealed record GateReplayRegateRequest(
    GateReplayRegateRoute Route,
    string GoalId,
    string AttemptId,
    string CheckIdentity,
    string CandidateSha,
    string ObservedMainSha)
{
    public const string RouteId = "operator-adjudicate-reopen-regate";
}

public sealed record GateReplayDescription(string Shell, string Command);

public sealed record GateReplayValidationResult(
    GateReplayDiagnostic Diagnostic,
    string Message,
    GateReplayDescription? Replay = null,
    GateReplayRegateRequest? Regate = null);

public static class GateReplayReceiptValidator
{
    public static GateReplayValidationResult Validate(GateReplayReceipt? receipt, GateReplayFacts facts)
    {
        if (receipt is null)
            return new(GateReplayDiagnostic.MissingReceipt,
                "The heartbeat has no replay receipt. The effective environment and acceptance authority inputs are missing.");
        if (!HasText(receipt.GoalId) || !HasText(receipt.AttemptId) || !HasText(receipt.CheckIdentity) ||
            receipt.RequiresAcceptanceLease is null || receipt.Arguments.IsDefault ||
            receipt.Arguments.Any(argument => argument is null || argument.Contains('\0')))
            return new(GateReplayDiagnostic.InvalidReceipt, "The check identity, arguments or acceptance lease binding is missing or invalid.");

        if (!IsGitSha(receipt.CandidateSha) || !SameHash(receipt.CandidateSha, facts.CandidateSha))
            return new(GateReplayDiagnostic.CandidateShaMismatch, "The candidate SHA is invalid or differs from the current candidate.");
        if (!IsGitSha(receipt.ObservedMainSha) || !SameHash(receipt.ObservedMainSha, facts.ObservedMainSha))
            return new(GateReplayDiagnostic.ObservedMainShaMismatch, "The observed main SHA is invalid or differs from current main.");
        if (!IsAbsolutePath(receipt.ExecutablePath) ||
            !StringComparer.Ordinal.Equals(receipt.ExecutablePath, facts.ExecutablePath))
            return new(GateReplayDiagnostic.ExecutablePathMismatch, "The executable path is invalid or differs from the observed executable.");
        if (!IsHex(receipt.ExecutableSha256, 64) || !SameHash(receipt.ExecutableSha256, facts.ExecutableSha256))
            return new(GateReplayDiagnostic.ExecutableHashMismatch, "The executable SHA-256 is invalid or differs from the observed executable.");

        if (ValidateArtifacts(receipt, facts.Artifacts) is { } artifactFailure)
            return artifactFailure;
        if (ValidateEnvironment(receipt) is { } environmentFailure)
            return environmentFailure;

        if (receipt.RequiresAcceptanceLease.Value)
            return new(GateReplayDiagnostic.AcceptanceLeaseRequired,
                $"This check requires an acceptance lease. Use {GateReplayRegateRequest.RouteId} and resolve the task number from the goal.",
                Regate: new(GateReplayRegateRoute.OperatorAdjudicateReopenRegate, receipt.GoalId, receipt.AttemptId,
                    receipt.CheckIdentity, receipt.CandidateSha, receipt.ObservedMainSha));

        if (!IsAbsolutePath(receipt.WorkingDirectory))
            return new(GateReplayDiagnostic.MissingWorkingDirectory, "The required working directory is missing or invalid.");
        return new(GateReplayDiagnostic.Ready, "The replay inputs match.", Describe(receipt));
    }

    private static GateReplayValidationResult? ValidateArtifacts(
        GateReplayReceipt receipt, IReadOnlyList<GateReplayArtifactFact> facts)
    {
        var artifacts = receipt.ExpectedArtifacts;
        if (artifacts.IsDefaultOrEmpty ||
            artifacts.Any(artifact => artifact is null || !Enum.IsDefined(artifact.Role) || !HasText(artifact.Path)) ||
            artifacts.Distinct().Count() != artifacts.Length ||
            artifacts.Count(artifact => artifact.Role == GateReplayArtifactRole.Stdout) != 1 ||
            artifacts.Count(artifact => artifact.Role == GateReplayArtifactRole.Stderr) != 1 ||
            artifacts.Count(artifact => artifact.Role == GateReplayArtifactRole.Timing) > 1)
            return new(GateReplayDiagnostic.InvalidReceipt, "The recorded output artifact inventory is incomplete or invalid.");

        foreach (var artifact in artifacts)
        {
            var matching = facts?.Where(fact => fact is not null && fact.Role == artifact.Role &&
                StringComparer.Ordinal.Equals(fact.Path, artifact.Path)).ToArray();
            if (matching is { Length: 1 } && matching[0].Exists) continue;

            if (matching is null or { Length: > 0 })
                return new(GateReplayDiagnostic.MissingArtifact, $"Missing or ambiguous {artifact.Role} artifact at '{artifact.Path}'.");

            var different = facts?.FirstOrDefault(fact => fact is not null && fact.Role == artifact.Role &&
                fact.Exists && !artifacts.Contains(new GateReplayArtifact(fact.Role, fact.Path)));
            if (different is not null)
                return new(GateReplayDiagnostic.ArtifactPathMismatch,
                    $"The {artifact.Role} artifact path differs. Expected '{artifact.Path}', observed '{different.Path}'.");

            return new(GateReplayDiagnostic.MissingArtifact, $"Missing {artifact.Role} artifact at '{artifact.Path}'.");
        }
        return null;
    }

    private static GateReplayValidationResult? ValidateEnvironment(GateReplayReceipt receipt)
    {
        var populated = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (receipt.Environment is { } environment)
        {
            foreach (var pair in environment)
            {
                if (!GateReplayEnvironmentAllowlist.Contains(pair.Key))
                    return new(GateReplayDiagnostic.InvalidEnvironment, "The receipt includes an environment name outside the replay allowlist.");
                if (pair.Value is null || pair.Value.Contains('\0') || GateReplayEnvironmentAllowlist.IsSensitive(pair.Value) ||
                    !populated.TryAdd(pair.Key, !string.IsNullOrWhiteSpace(pair.Value)))
                    return new(GateReplayDiagnostic.InvalidEnvironment, $"The receipt has an invalid environment entry for {pair.Key}.");
            }
        }

        var missing = GateReplayEnvironmentAllowlist.RequiredNames.FirstOrDefault(name => !populated.GetValueOrDefault(name));
        return missing is null ? null :
            new(GateReplayDiagnostic.MissingRequiredEnvironment, $"The required environment entry {missing} is missing or blank.");
    }

    private static GateReplayDescription Describe(GateReplayReceipt receipt)
    {
        // ProcessStartInfo.ArgumentList preserves empty arguments and quoting across PowerShell versions.
        var script = new StringBuilder();
        script.AppendLine($"Set-Location -LiteralPath {Quote(receipt.WorkingDirectory)} -ErrorAction Stop");
        script.AppendLine("$launch = [System.Diagnostics.ProcessStartInfo]::new()");
        script.AppendLine("$launch.UseShellExecute = $false");
        script.AppendLine($"$launch.WorkingDirectory = {Quote(receipt.WorkingDirectory)}");
        foreach (var name in GateReplayEnvironmentAllowlist.Names.Except(receipt.Environment.Keys, StringComparer.OrdinalIgnoreCase))
            script.AppendLine($"[void]$launch.Environment.Remove({Quote(name)})");
        foreach (var pair in receipt.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            script.AppendLine($"$launch.Environment[{Quote(pair.Key)}] = {Quote(pair.Value)}");
        script.AppendLine($"$launch.FileName = {Quote(receipt.ExecutablePath)}");
        foreach (var argument in receipt.Arguments)
            script.AppendLine($"$launch.ArgumentList.Add({Quote(argument)})");
        script.AppendLine("$replayProcess = [System.Diagnostics.Process]::Start($launch)");
        script.AppendLine("try { $replayProcess.WaitForExit(); $global:LASTEXITCODE = $replayProcess.ExitCode }");
        script.Append("finally { $replayProcess.Dispose() }");
        return new("PowerShell 7+", script.ToString());
    }

    private static string Quote(string value) => "'" + value
        .Replace("'", "''", StringComparison.Ordinal)
        .Replace("‘", "‘‘", StringComparison.Ordinal)
        .Replace("’", "’’", StringComparison.Ordinal)
        .Replace("‚", "‚‚", StringComparison.Ordinal)
        .Replace("‛", "‛‛", StringComparison.Ordinal) + "'";

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Contains('\0');
    private static bool IsAbsolutePath(string? value) => HasText(value) && Path.IsPathFullyQualified(value!);
    private static bool IsGitSha(string? value) => IsHex(value, 40) || IsHex(value, 64);
    private static bool IsHex(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);
    private static bool SameHash(string captured, string? observed) =>
        StringComparer.OrdinalIgnoreCase.Equals(captured, observed);
}
