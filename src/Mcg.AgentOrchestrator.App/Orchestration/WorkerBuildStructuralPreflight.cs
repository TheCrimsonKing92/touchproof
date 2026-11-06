using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns invocation binding and completeness, not source rules or limits.
internal sealed class WorkerBuildStructuralPreflight(WorkerBuildHostReference reference,
    Func<DeveloperCompletionStructuralFindings>? evaluate = null, TimeProvider? time = null)
{
    // Cold unbounded run at 88da88ef8 (2026-11-05): 16,996 ms total, 11,464 ms
    // test-class scan. Keep 13,004 ms headroom without assuming parallel speedup.
    // This is specific to worker builds; the conductor's synchronous bound is separate.
    internal static readonly TimeSpan EvaluationBound = TimeSpan.FromSeconds(30);

    internal static WorkerBuildStructuralPolicy CapturePolicy(TaskDispatchRecord dispatch,
        string? projectHomeDirectory, string integrationBranch)
    {
        try { return CaptureRequiredPolicy(dispatch, projectHomeDirectory, integrationBranch); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // Policy discovery is evidence preparation, not dispatch-start authority.
            // Enroll the unresolved context so the build response can report it honestly.
            return new(projectHomeDirectory, integrationBranch, false, false,
                UnavailableDiagnostic: $"{error.GetType().Name}: {error.Message}");
        }
    }

    private static WorkerBuildStructuralPolicy CaptureRequiredPolicy(TaskDispatchRecord dispatch,
        string? projectHomeDirectory, string integrationBranch)
    {
        var baseline = dispatch.WorkerBuildScope?.GoalBaseline ?? dispatch.BaseCommit
            ?? throw new InvalidOperationException("Structural enrollment needs the dispatch baseline.");
        var paths = GitCli.RunForRepository(dispatch.WorkingDirectory, "ls-tree", "-r", "--name-only", baseline, "--",
            SourceSizeRatchet.SourcePath, "config/acceptance-manifest.json", ".orchestrator/acceptance-manifest.json");
        if (!paths.Succeeded || paths.DrainTimedOut || !paths.ProcessStarted)
            throw new IOException("Structural enrollment could not read baseline policy membership.");
        var entries = paths.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var home = projectHomeDirectory is null ? null : Path.GetFullPath(projectHomeDirectory);
        var homeManifest = home is null ? null : Path.Combine(home, "acceptance-manifest.json");
        var manifest = homeManifest is not null && File.Exists(homeManifest) ? homeManifest
            : AcceptanceManifestLocator.SearchedLocations(dispatch.WorkingDirectory)
                .Select(path => Path.GetRelativePath(dispatch.WorkingDirectory, path).Replace('\\', '/'))
                .FirstOrDefault(entries.Contains);
        return new(home, integrationBranch, entries.Contains(SourceSizeRatchet.SourcePath), manifest is not null, manifest, baseline);
    }

    internal string PolicyDigest(string root)
    {
        var paths = AcceptanceManifestLocator.SearchedLocations(root, reference.StructuralPolicy?.ProjectHomeDirectory)
            .Append(Path.Combine(root, SourceSizeRatchet.SourcePath));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(reference.StructuralPolicy));
        foreach (var path in paths)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFullPath(path) + "\0"));
            // Open directly: access failures must not be mistaken for absence by File.Exists.
            try
            {
                var bytes = File.ReadAllBytes(path);
                hash.AppendData([1]);
                hash.AppendData(bytes);
            }
            catch (FileNotFoundException) { hash.AppendData([0]); }
            catch (DirectoryNotFoundException) { hash.AppendData([0]); }
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal WorkerBuildStructuralCheck Run(WorkerBuildInvocation invocation, string policyDigest,
        Action validateCurrent, CancellationToken lifetime)
    {
        var clock = time ?? TimeProvider.System;
        double? elapsedMilliseconds = null;
        try
        {
            lifetime.ThrowIfCancellationRequested();
            validateCurrent();
            if (reference.StructuralPolicy is not { } policy)
                return Unavailable(invocation, policyDigest, "Host enrollment has no structural policy context.");
            if (policy.UnavailableDiagnostic is { } diagnostic)
                return Unavailable(invocation, policyDigest, diagnostic);
            DeveloperCompletionStructuralFindings findings;
            var started = clock.GetTimestamp();
            try
            {
                findings = BoundedStructuralPreflight.Run(() =>
                {
                    if (policy.RequiresSizeAuthority && !File.Exists(Path.Combine(invocation.WorktreeRoot, SourceSizeRatchet.SourcePath)))
                        throw new IOException($"Required structural authority is missing: {SourceSizeRatchet.SourcePath}");
                    if (policy.RequiresManifest && (policy.RequiredManifestPath is null ||
                        !File.Exists(Path.Combine(invocation.WorktreeRoot, policy.RequiredManifestPath))))
                        throw new IOException($"Required structural manifest is missing: {policy.RequiredManifestPath}");
                    return evaluate is null
                        ? DeveloperCompletionStructuralPreflight.Evaluate(invocation.WorktreeRoot, policy.ProjectHomeDirectory,
                            policy.IntegrationBranch)
                        : evaluate();
                }, bound: EvaluationBound, cancellationToken: lifetime, time: clock);
            }
            finally { elapsedMilliseconds = clock.GetElapsedTime(started).TotalMilliseconds; }
            lifetime.ThrowIfCancellationRequested();
            validateCurrent();
            if (PolicyDigest(invocation.WorktreeRoot) != policyDigest)
                return Skipped(invocation, policyDigest, "Structural policy changed during evaluation.", elapsedMilliseconds);
            if (findings is null || string.IsNullOrWhiteSpace(findings.Message))
                return Unavailable(invocation, policyDigest, "Structural evaluator returned malformed findings.", elapsedMilliseconds);
            if (!findings.EvaluationComplete)
                return Unavailable(invocation, policyDigest, findings.UnavailableDiagnostic ?? findings.Message, elapsedMilliseconds);
            if (!findings.IsApplicable && (policy.RequiresSizeAuthority || policy.RequiresManifest))
                return Unavailable(invocation, policyDigest, "Required structural evaluator declined applicability.", elapsedMilliseconds);
            return Bound(invocation, policyDigest, findings.HasViolation ? WorkerBuildStructuralStatus.Violation
                : findings.IsApplicable ? WorkerBuildStructuralStatus.Passed : WorkerBuildStructuralStatus.NotApplicable, findings.Message, elapsedMilliseconds);
        }
        catch (WorkerBuildRequestHost.WorkerBuildCandidateChangedException error)
        {
            return Skipped(invocation, policyDigest, $"{error.GetType().Name}: {error.Message}", elapsedMilliseconds);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Unavailable(invocation, policyDigest, error is TimeoutException
                ? $"Structural evaluation exceeded {EvaluationBound.TotalMilliseconds:0} ms bound."
                : $"{error.GetType().Name}: {error.Message}", elapsedMilliseconds);
        }
    }

    internal WorkerBuildStructuralCheck Skipped(WorkerBuildInvocation invocation, string policyDigest, string diagnostic,
        double? elapsedMilliseconds = null) =>
        Bound(invocation, policyDigest, WorkerBuildStructuralStatus.Unavailable, diagnostic, elapsedMilliseconds);

    internal WorkerBuildStructuralCheck Unavailable(WorkerBuildInvocation invocation, string policyDigest, string diagnostic,
        double? elapsedMilliseconds = null) =>
        Bound(invocation, policyDigest, WorkerBuildStructuralStatus.Unavailable, diagnostic +
            " Report this unavailable check and host diagnostic; host/operator owns recovery. Do not edit unrelated source or repeatedly rebuild.", elapsedMilliseconds);

    private WorkerBuildStructuralCheck Bound(WorkerBuildInvocation invocation, string policyDigest,
        WorkerBuildStructuralStatus status, string diagnostic, double? elapsedMilliseconds = null) => new(status, diagnostic,
            invocation.Id, invocation.DispatchId, invocation.TreeDigest, invocation.ScopeIdentity,
            reference.StructuralPolicy?.ProjectHomeDirectory, policyDigest, elapsedMilliseconds);
}
