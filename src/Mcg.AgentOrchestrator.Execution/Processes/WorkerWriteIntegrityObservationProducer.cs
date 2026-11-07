using System.ComponentModel;
using System.Security;
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

    internal static TaskVerificationRecord Enrich(TaskSpec task, IWorkerIntegrityLabeler labeler, TaskVerificationRecord verification) =>
        task.RequiredRole == AgentRole.Developer && verification.FullStandardOutput is { } output &&
        TryObserve(labeler, task.LastDispatch?.WorkingDirectory ?? verification.WorkingDirectory, output, out var observation)
            ? verification with { WriteIntegrityObservation = observation }
            : verification;

    internal static bool TryObserve(IWorkerIntegrityLabeler labeler, string worktree,
        string workerOutput, out WorkerWriteIntegrityObservation? observation)
    {
        observation = null;
        try
        {
            if (!WorkerResultBlockers.TryFindBlocker(workerOutput, out var blocker) ||
                !blocker.StartsWith("exact-blocker - ", StringComparison.OrdinalIgnoreCase) ||
                !Denial.IsMatch(blocker))
                return false;

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var root = Path.GetFullPath(worktree);
            // Quoted paths may contain spaces. Bare filenames are accepted; prose is not a target.
            var pathTokens = Tokens.Matches(blocker)
                .Where(match => IsPathToken(match.Groups["path"].Value.TrimEnd('.', ':'))).ToArray();
            var candidates = pathTokens.Select(match => Path.GetFullPath(match.Groups["path"].Value.TrimEnd('.', ':'), root))
                .Distinct(comparer).ToArray();
            if (candidates.Length != 1 || !IsWithin(candidates[0], root, comparison))
                return false;
            if (!pathTokens.Any(path => Denial.Matches(blocker).Any(denial => IsAssociated(blocker, path, denial))))
                return false;

            var target = ResolveTarget(candidates[0]);
            if (!IsWithin(target, ResolveTarget(root), comparison))
                return false;

            IntegrityLabelState state;
            try
            {
                state = labeler.Query(target);
            }
            catch (Exception ex) when (IsExpectedFailure(ex))
            {
                observation = new(Path.GetRelativePath(root, candidates[0]), WorkerWriteIntegrityLabel.Other, false);
                return true;
            }

            // The blocker never supplies a label. Contradictory and unordered native states
            // do not establish an integrity level above Low.
            var label = (state.Low, state.Medium, state.Unlabeled) switch
            {
                (true, false, false) => WorkerWriteIntegrityLabel.Low,
                (false, true, false) => WorkerWriteIntegrityLabel.Medium,
                (false, false, true) => WorkerWriteIntegrityLabel.Unlabeled,
                _ => WorkerWriteIntegrityLabel.Other
            };
            observation = new(Path.GetRelativePath(root, candidates[0]), label,
                state.Exists && state.NativeQueryError is null);
            return true;
        }
        catch (Exception ex) when (IsExpectedFailure(ex) || ex is RegexMatchTimeoutException)
        {
            return false;
        }
    }

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
