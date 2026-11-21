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
    ImmutableArray<string> DroppedEnvironmentNames)
{
    public int DroppedEnvironmentCount => DroppedEnvironmentNames.IsDefault ? 0 : DroppedEnvironmentNames.Length;

    internal static Launch? CaptureLaunch(
        AcceptanceProcessRequest request,
        ProcessStartInfo startInfo,
        bool directLaunch,
        string[] checkArguments)
    {
        if (request.HeartbeatContext?.AttemptIdentity is null || request.SuppressDiagnosticCaptures || checkArguments.Length == 0)
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
                [.. checkArguments.Skip(1)], GateReplayEnvironmentAllowlist.Capture(startInfo.Environment));
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
        GateReplayEnvironmentAllowlist.Projection Environment)
    {
        internal GateReplayReceipt? Bind(GateHeartbeatContext context, string stdoutPath, string stderrPath)
        {
            if (context.AttemptIdentity is not { } attempt) return null;

            var artifacts = ImmutableArray.CreateBuilder<GateReplayArtifact>();
            artifacts.Add(new(GateReplayArtifactRole.Stdout, stdoutPath));
            artifacts.Add(new(GateReplayArtifactRole.Stderr, stderrPath));
            foreach (var path in context.DeclaredTestResultPaths ?? [])
                artifacts.Add(new(GateReplayArtifactRole.TestResults, path));
            if (Environment.Values.TryGetValue(LaneSlotTimingReader.VariableName, out var timingPath) &&
                !string.IsNullOrWhiteSpace(timingPath))
                artifacts.Add(new(GateReplayArtifactRole.Timing, timingPath));

            return new GateReplayReceipt(attempt.GoalId, attempt.VerifyingCommitSha, attempt.MainSha, attempt.AttemptId,
                context.CurrentTarget, WorkingDirectory, ExecutablePath, ExecutableSha256, Arguments,
                artifacts.ToImmutable(), Environment.Values, context.RequiresAcceptanceLease, Environment.DroppedNames);
        }
    }
}
