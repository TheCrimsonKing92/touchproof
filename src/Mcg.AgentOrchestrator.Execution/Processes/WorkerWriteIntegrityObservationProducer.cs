using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerWriteIntegrityObservationProducer
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex Denial = new(
        @"\b(?:cannot[\s-]+write|access[\s-]+denied|integrity[\s-]+mismatch)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex Tokens = new(
        "\"(?<path>[^\"\\r\\n]+)\"|'(?<path>[^'\\r\\n]+)'|`(?<path>[^`\\r\\n]+)`|(?<path>[^\\s\"'`<>();,]+)",
        RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex Association = new(
        @"^(?:[\s:=()\-]|\b(?:to|the|file|path|for|on|at|prevents|editing|writing|of|target|is|because|due)\b)*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    internal const string CollectionPrefix = "write-denials ";
    internal const int MaxTargets = 8;
    internal const int MaxReportCharacters = 16 * 1024;
    internal const int MaxOutputCharacters = 4 * 1024 * 1024;

    internal static TaskVerificationRecord Enrich(TaskSpec task, IWorkerIntegrityLabeler labeler, TaskVerificationRecord verification,
        DispatchProcessHost.WorkerExecutionIntegrityObservation? launch = null)
    {
        if (task.RequiredRole != AgentRole.Developer || verification.FullStandardOutput is not { } output ||
            !TryObserveAll(labeler, task.LastDispatch?.WorkingDirectory ?? verification.WorkingDirectory,
                output, out var observations, out var blocker))
            return verification;

        var dispatch = task.LastDispatch;
        var gap = launch is null
            ? "Actual Low execution is unproven: host launch provenance does not record an observed execution integrity level."
            : launch.Low != true
                ? $"Actual Low execution is unproven: {launch.Diagnostic}"
                : dispatch is null || verification.DispatchStartedAt is null ||
                  string.IsNullOrWhiteSpace(dispatch.WorktreeHeadSha) || string.IsNullOrWhiteSpace(dispatch.DirtyStateHash) ||
                  launch.Identity is not { } identity || launch.TaskId != task.Id.Value ||
                  launch.DispatchAttemptAt != dispatch?.DispatchedAt || launch.WorktreeHeadSha != dispatch?.WorktreeHeadSha ||
                  launch.DirtyStateHash != dispatch?.DirtyStateHash ||
                  (launch.CompletionChildProcessId ?? identity.ProcessId) != verification.ChildProcessId ||
                  identity.StartedAt < verification.DispatchStartedAt || launch.ObservedAt < identity.StartedAt ||
                  launch.ObservedAt > verification.CompletedAt
                    ? $"Actual Low execution is unproven: host token observation does not match this dispatch/candidate/child. {launch.Diagnostic}"
                    : null;
        var lowLaunch = gap is null && launch?.Identity is { } provenIdentity
            ? new WorkerLowIntegrityLaunchEvidence(task.Id, dispatch!.DispatchedAt, verification.DispatchStartedAt ?? default,
                dispatch.WorktreeHeadSha!, dispatch.DirtyStateHash!, provenIdentity.ProcessId, provenIdentity.StartedAt,
                provenIdentity.ImagePath, 4096, launch.Diagnostic, launch.CompletionChildProcessId)
            : null;
        return verification with
        {
            WriteIntegrityObservation = observations.Count == 1 ? observations[0] : null,
            WriteIntegrityEvidence = new(task.Id, dispatch?.DispatchedAt ?? default,
                verification.DispatchStartedAt ?? default, dispatch?.WorktreeHeadSha, dispatch?.DirtyStateHash,
                blocker, observations, LowLaunch: lowLaunch, EvidenceGap: gap)
        };
    }

    internal static DispatchProcessHost.WorkerExecutionIntegrityObservation CaptureLaunch(Process child,
        DispatchProcessHost.DispatchRunParameters parameters, Func<Process, bool> isOwned,
        Func<Process, bool>? readLow = null)
    {
        SpawnProcessIdentity? identity = null;
        bool? low = null;
        string diagnostic;
        try
        {
            // Retain the handle while reading identity and token; a recycled PID is never proof.
            _ = child.SafeHandle;
            identity = DispatchProcessHost.ReadHeartbeatProcessIdentity(child.Id, child);
            if (child.HasExited || identity is null || !isOwned(child))
                diagnostic = "Native token query unavailable: launched worker is not a live identity-confirmed owned process.";
            else if (readLow is null && !OperatingSystem.IsWindows())
                diagnostic = "Native token query unavailable: Windows token integrity inspection is unsupported on this host.";
            else
            {
                var observedLow = (readLow ?? WorkerBuildProcessIntegrity.IsLow)(child);
                if (child.HasExited || !isOwned(child))
                    diagnostic = "Native token query unproven: launched worker exited or ownership became unavailable during inspection.";
                else
                {
                    low = observedLow;
                    diagnostic = observedLow
                        ? "Native execution token query confirmed S-1-16-4096 on the live owned worker handle."
                        : "Native execution token query confirmed the live owned worker token is not S-1-16-4096.";
                }
            }
        }
        catch (Exception ex) when (IsExpectedFailure(ex) || ex is InvalidOperationException)
        {
            diagnostic = $"Native token query unavailable: {ex.GetType().Name}: {ex.Message}";
        }
        return new(parameters.WorkerTaskId, parameters.DispatchAttemptAt, parameters.WorktreeHeadSha,
            parameters.DirtyStateHash, identity, DateTimeOffset.UtcNow, low, diagnostic);
    }

    internal static bool TryObserve(IWorkerIntegrityLabeler labeler, string worktree,
        string workerOutput, out WorkerWriteIntegrityObservation? observation)
    {
        observation = null;
        if (!TryObserveAll(labeler, worktree, workerOutput, out var observations, out _) || observations.Count != 1)
            return false;
        observation = observations[0];
        return true;
    }

    internal static bool TryObserveAll(IWorkerIntegrityLabeler labeler, string worktree, string workerOutput,
        out IReadOnlyList<WorkerWriteIntegrityObservation> observations, out string blocker)
    {
        observations = [];
        blocker = string.Empty;
        try
        {
            if (workerOutput.Length > MaxOutputCharacters ||
                !WorkerResultBlockers.TryFindBlocker(workerOutput, out blocker) ||
                blocker.Length > MaxReportCharacters ||
                !blocker.StartsWith("exact-blocker - ", StringComparison.OrdinalIgnoreCase))
                return false;

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var root = Path.GetFullPath(worktree);
            var report = blocker["exact-blocker - ".Length..];
            List<DeniedTarget> entries;
            if (report.StartsWith(CollectionPrefix, StringComparison.Ordinal))
            {
                if (!TryParseCollection(report[CollectionPrefix.Length..], out entries))
                    return false;
            }
            else
            {
                if (!Denial.IsMatch(blocker)) return false;
                var singleBlocker = blocker;
                // Preserve the existing single-target grammar, including its bare-filename support.
                var pathTokens = Tokens.Matches(blocker)
                    .Where(match => IsPathToken(match.Groups["path"].Value.TrimEnd('.', ':'))).ToArray();
                var candidates = pathTokens.Select(match => Path.GetFullPath(match.Groups["path"].Value.TrimEnd('.', ':'), root))
                    .Distinct(comparer).ToArray();
                if (candidates.Length != 1 ||
                    !pathTokens.Any(path => Denial.Matches(singleBlocker).Any(denial => IsAssociated(singleBlocker, path, denial))))
                    return false;
                entries = [new(pathTokens[0].Groups["path"].Value.TrimEnd('.', ':'), blocker)];
            }

            // Validate the complete set before querying any member. No invalid member may disappear.
            var resolvedRoot = ResolveTarget(root);
            var targets = new List<(DeniedTarget Entry, string Path, string Resolved)>();
            var identities = new HashSet<string>(comparer);
            var resolvedIdentities = new HashSet<string>(comparer);
            foreach (var entry in entries)
            {
                if (entry.Path.Length > 1024 || entry.Path.Any(char.IsControl) ||
                    entry.Path.StartsWith(@"\\?\", StringComparison.Ordinal) || entry.Path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                    OperatingSystem.IsWindows() && entry.Path.Split(['/', '\\']).Any(component =>
                        component is not ("." or "..") && (component.EndsWith('.') || component.EndsWith(' '))))
                    return false;
                var path = Path.GetFullPath(entry.Path, root);
                if (path.Equals(root, comparison) || !IsWithin(path, root, comparison) || IsGitTarget(path, root))
                    return false;
                var resolved = ResolveTarget(path);
                if (!IsWithin(resolved, resolvedRoot, comparison) || IsGitTarget(resolved, resolvedRoot) ||
                    !identities.Add(path) || !resolvedIdentities.Add(resolved))
                    return false;
                targets.Add((entry, path, resolved));
            }

            var results = new List<WorkerWriteIntegrityObservation>();
            foreach (var target in targets)
            {
                IntegrityLabelState state;
                try
                {
                    state = labeler.Query(target.Resolved);
                }
                catch (Exception ex) when (IsExpectedFailure(ex))
                {
                    results.Add(new(Path.GetRelativePath(root, target.Path), WorkerWriteIntegrityLabel.Other, false)
                    { ReportedPath = target.Entry.Path, OsDiagnostic = target.Entry.Diagnostic, NativeDiagnostic = ex.Message });
                    continue;
                }

                var label = (state.Low, state.Medium, state.Unlabeled) switch
                {
                    (true, false, false) => WorkerWriteIntegrityLabel.Low,
                    (false, true, false) => WorkerWriteIntegrityLabel.Medium,
                    (false, false, true) => WorkerWriteIntegrityLabel.Unlabeled,
                    _ => WorkerWriteIntegrityLabel.Other
                };
                results.Add(new(Path.GetRelativePath(root, target.Path), label, state.Exists && state.NativeQueryError is null)
                {
                    ReportedPath = target.Entry.Path, OsDiagnostic = target.Entry.Diagnostic,
                    NativeQueryError = state.NativeQueryError,
                    NativeDiagnostic = $"exists={state.Exists}; low={state.Low}; medium={state.Medium}; unlabeled={state.Unlabeled}; nativeError={state.NativeQueryError}"
                });
            }
            observations = results;
            return true;
        }
        catch (Exception ex) when (IsExpectedFailure(ex) || ex is RegexMatchTimeoutException or JsonException)
        {
            return false;
        }
    }

    private sealed record DeniedTarget(string Path, string Diagnostic);

    private static bool TryParseCollection(string report, out List<DeniedTarget> entries)
    {
        entries = [];
        try
        {
            using var document = JsonDocument.Parse(report, new JsonDocumentOptions { MaxDepth = 4 });
            var array = document.RootElement;
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > MaxTargets)
                return false;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return false;
                var properties = item.EnumerateObject().ToArray();
                if (properties.Length != 3 || properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != 3 ||
                    properties.Any(property => property.Name is not ("kind" or "path" or "osDiagnostic") || property.Value.ValueKind != JsonValueKind.String) ||
                    item.GetProperty("kind").GetString() != "filesystem-write-denied")
                    return false;
                var path = item.GetProperty("path").GetString()!;
                var diagnostic = item.GetProperty("osDiagnostic").GetString()!;
                if (string.IsNullOrWhiteSpace(path) || path != path.Trim() ||
                    !Path.IsPathFullyQualified(path) && !(path.Contains('/') || path.Contains('\\')) ||
                    diagnostic.Length > 2048 || string.IsNullOrWhiteSpace(diagnostic) ||
                    path.Contains(':') && !(Path.IsPathFullyQualified(path) && path.IndexOf(':') == 1 && path.LastIndexOf(':') == 1))
                    return false;
                entries.Add(new(path, diagnostic));
            }
            return true;
        }
        catch (InvalidOperationException)
        {
            // JsonDocument can parse escaped lone surrogates that throw when names or values are decoded.
            return false;
        }
    }

    private static bool IsGitTarget(string path, string root) =>
        Path.GetRelativePath(root, path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
            .Any(component => component.Equals(".git", StringComparison.OrdinalIgnoreCase));

    private static bool IsPathToken(string token) =>
        token.Contains('/') || token.Contains('\\') || Path.HasExtension(token);

    private static bool IsAssociated(string blocker, Match path, Match denial)
    {
        var start = path.Index < denial.Index ? path.Index + path.Length : denial.Index + denial.Length;
        var end = path.Index < denial.Index ? denial.Index : path.Index;
        return start <= end && Association.IsMatch(blocker[start..end]);
    }

    private static bool IsWithin(string path, string root, StringComparison comparison) =>
        path.Equals(root, comparison) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) +
            Path.DirectorySeparatorChar, comparison);

    private static string ResolveTarget(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var component in path[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException("Blocked-write target link could not be resolved.");
        }
        return current;
    }

    private static bool IsExpectedFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException or Win32Exception;
}
