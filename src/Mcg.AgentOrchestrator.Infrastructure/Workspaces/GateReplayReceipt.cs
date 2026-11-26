using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Infrastructure;

[JsonConverter(typeof(JsonStringEnumConverter<GateReplayArtifactRole>))]
public enum GateReplayArtifactRole
{
    [JsonStringEnumMemberName("stdout")] Stdout,
    [JsonStringEnumMemberName("stderr")] Stderr,
    [JsonStringEnumMemberName("test-results")] TestResults,
    [JsonStringEnumMemberName("timing")] Timing
}

public sealed record GateReplayArtifact(GateReplayArtifactRole Role, string Path);

public sealed record GateReplayReceipt(
    string GoalId,
    string CandidateSha,
    string ObservedMainSha,
    string AttemptId,
    string CheckIdentity,
    string WorkingDirectory,
    string ExecutablePath,
    string ExecutableSha256,
    ImmutableArray<string> Arguments,
    ImmutableArray<GateReplayArtifact> ExpectedArtifacts,
    ImmutableDictionary<string, string> Environment,
    bool? RequiresAcceptanceLease,
    ImmutableArray<string> DroppedEnvironmentNames,
    int? PathLength = null,
    bool? SystemDirectoryOnPath = null,
    bool? WindowsPowerShellDirectoryOnPath = null,
    bool? GitDirectoryOnPath = null,
    bool? DotnetDirectoryOnPath = null)
{
    public int DroppedEnvironmentCount => DroppedEnvironmentNames.IsDefault ? 0 : DroppedEnvironmentNames.Length;

    internal sealed record LaunchIdentity(string GoalId, string CandidateSha, string MainSha, string RunId);

    internal static LaunchIdentity? ResolveFocusedLaunchIdentity(IAcceptanceRunExecutionContext? context) => context switch
    {
        AcceptanceRunExecutionContextView view => ResolveFocusedLaunchIdentity(view.Owner),
        AcceptanceFocusedVerificationOwner { FocusedRunBinding.ReplayObservedMainSha: { } main } owner =>
            new(owner.Identity.GoalId, owner.Identity.CandidateSha, main, owner.Identity.FocusedRunId),
        _ => null
    };

    internal static Launch? CaptureLaunch(
        AcceptanceProcessRequest request,
        ProcessStartInfo startInfo,
        bool directLaunch,
        string[] checkArguments)
    {
        if (request.HeartbeatContext is not ({ AttemptIdentity: not null } or { FocusedReplayIdentity: not null }) ||
            request.SuppressDiagnosticCaptures || checkArguments.Length == 0)
            return null;

        // Windows shell dispatch can select another image. On POSIX the shell receives a
        // single-quoted command, so an explicitly absolute target is still bound.
        var executablePath = directLaunch ? startInfo.FileName : checkArguments[0];
        if ((!directLaunch && OperatingSystem.IsWindows()) || !Path.IsPathFullyQualified(executablePath))
            return null;

        try
        {
            using var executable = File.OpenRead(executablePath);
            var executableSha256 = Convert.ToHexStringLower(SHA256.HashData(executable));
            return new Launch(startInfo.WorkingDirectory, executablePath, executableSha256,
                [.. checkArguments.Skip(1)], GateReplayEnvironmentAllowlist.Capture(startInfo.Environment),
                GateToolResolutionPreflight.Current);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            // An unreadable image cannot support replay. Capture is observational.
            return null;
        }
    }

    internal sealed record Launch(
        string WorkingDirectory,
        string ExecutablePath,
        string ExecutableSha256,
        ImmutableArray<string> Arguments,
        GateReplayEnvironmentAllowlist.Projection Environment,
        GateToolResolutionResult? ToolResolution = null)
    {
        internal GateReplayReceipt? Bind(GateHeartbeatContext context, string stdoutPath, string stderrPath)
        {
            var identity = context.AttemptIdentity is { } attempt
                ? new LaunchIdentity(attempt.GoalId, attempt.VerifyingCommitSha, attempt.MainSha, attempt.AttemptId)
                : context.FocusedReplayIdentity;
            if (identity is null) return null;

            var artifacts = ImmutableArray.CreateBuilder<GateReplayArtifact>();
            artifacts.Add(new(GateReplayArtifactRole.Stdout, stdoutPath));
            artifacts.Add(new(GateReplayArtifactRole.Stderr, stderrPath));
            foreach (var path in context.DeclaredTestResultPaths ?? [])
                artifacts.Add(new(GateReplayArtifactRole.TestResults, path));
            if (Environment.Values.TryGetValue(LaneSlotTimingReader.VariableName, out var timingPath) &&
                !string.IsNullOrWhiteSpace(timingPath))
                artifacts.Add(new(GateReplayArtifactRole.Timing, timingPath));

            return new GateReplayReceipt(identity.GoalId, identity.CandidateSha, identity.MainSha, identity.RunId,
                context.CurrentTarget, WorkingDirectory, ExecutablePath, ExecutableSha256, Arguments,
                artifacts.ToImmutable(), Environment.Values, context.RequiresAcceptanceLease, Environment.DroppedNames,
                ToolResolution?.PathLength, ToolResolution?.SystemDirectoryOnPath,
                ToolResolution?.WindowsPowerShellDirectoryOnPath, ToolResolution?.GitDirectoryOnPath,
                ToolResolution?.DotnetDirectoryOnPath);
        }
    }
}
