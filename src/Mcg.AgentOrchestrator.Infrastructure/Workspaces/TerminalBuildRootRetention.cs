using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A root becomes disposable only after the stored goal and its lease both prove eligibility.
internal sealed class TerminalBuildRootRetention(
    DotnetBuildStorageRoot storageRoot,
    IOwnedRunRootStore ownedRoots,
    Func<int, bool> isOwnerRunning,
    TimeProvider timeProvider,
    Func<string, TerminalBuildRootRetainedBytes.Measurement>? measureRoot = null)
{
    public TerminalBuildRootRetentionResult Run(
        IReadOnlyList<StoredGoalRetentionFact> goals, int retentionDays, int maxReclaims, long? maxBytes = null)
    {
        var skipped = new Dictionary<TerminalBuildRootSkipReason, int>();
        var reclaimed = new List<string>();
        var retained = new TerminalBuildRootRetainedBytes();
        long bytes = 0;
        ArgumentOutOfRangeException.ThrowIfNegative(retentionDays);
        ArgumentOutOfRangeException.ThrowIfNegative(maxReclaims);
        if (maxBytes is < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (retentionDays == 0 && maxBytes is null)
            return new(0, 0, skipped, reclaimed, string.Empty, retained);
        ArgumentNullException.ThrowIfNull(isOwnerRunning);
        var now = timeProvider.GetUtcNow();
        var cutoff = now.AddDays(-retentionDays);
        var reclaimFolder = Path.Combine(storageRoot.RootPath, "goal-root-reclaim");
        var attempts = 0;
        var byPrefix = goals.Where(goal => goal.GoalId.Length >= 8)
            .GroupBy(goal => goal.GoalId[..8], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        void Skip(TerminalBuildRootSkipReason reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;

        bool Remove(string renamed, string original)
        {
            try
            {
                // Never traverse links when measuring the detached tree.
                var size = Directory.EnumerateFiles(renamed, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint,
                    IgnoreInaccessible = false
                }).Sum(path => new FileInfo(path).Length);
                Directory.Delete(renamed, recursive: true);
                bytes += size;
                reclaimed.Add(original);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Skip(TerminalBuildRootSkipReason.RemovalFailed);
                return false;
            }
        }

        string[] Children(string folder)
        {
            try
            {
                if (Directory.Exists(folder) && !IsContainedDirectory(folder))
                {
                    Skip(TerminalBuildRootSkipReason.NotContained);
                    return [];
                }
                return Directory.Exists(folder)
                    ? Directory.GetDirectories(folder).OrderBy(path => path, StringComparer.Ordinal).ToArray()
                    : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Skip(TerminalBuildRootSkipReason.NotContained);
                return [];
            }
        }

        foreach (var leftover in Children(reclaimFolder))
        {
            if (!IsContainedDirectory(leftover))
                Skip(TerminalBuildRootSkipReason.NotContained);
            else if (attempts >= maxReclaims)
                Skip(TerminalBuildRootSkipReason.BudgetExhausted);
            else
            {
                attempts++;
                Remove(leftover, leftover);
            }
        }

        var measure = measureRoot ?? TerminalBuildRootRetainedBytes.Measure;
        var roots = Children(Path.Combine(storageRoot.RootPath, "goals"))
            .Select(root => (Root: root, Size: measure(root))).ToArray();
        var remainingBytes = roots.Sum(root => root.Size.Bytes ?? 0);
        var categories = new Dictionary<string, TerminalBuildRootRetainedBytes.Category>();
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<(string Root, TerminalBuildRootRetainedBytes.Measurement Size,
            DateTimeOffset LastUsed, bool WithinWindow, bool OwnerLive)>();

        foreach (var (root, size) in roots)
        {
            var prefix = Path.GetFileName(root);
            // The first three decisions intentionally inspect only stored facts, not root contents.
            if (!byPrefix.TryGetValue(prefix, out var facts))
                Skip(TerminalBuildRootSkipReason.GoalNotStored);
            else if (facts.Select(fact => fact.GoalId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                Skip(TerminalBuildRootSkipReason.GoalPrefixAmbiguous);
            else if (facts.Any(fact => fact.Status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded)))
            {
                Skip(TerminalBuildRootSkipReason.GoalNotTerminal);
                categories[root] = TerminalBuildRootRetainedBytes.Category.NotTerminal;
            }
            else if (!IsContainedDirectory(root) ||
                (Directory.Exists(reclaimFolder) && !IsContainedDirectory(reclaimFolder)))
                Skip(TerminalBuildRootSkipReason.NotContained);
            else if (!TryReadLease(root, facts[0].GoalId, out var owner, out var lastUsed))
                Skip(TerminalBuildRootSkipReason.LeaseUnreadable);
            else if (ownedRoots.ReadRegisteredPaths([root]).Contains(root))
            {
                Skip(TerminalBuildRootSkipReason.RegisteredOwnedRoot);
                categories[root] = TerminalBuildRootRetainedBytes.Category.RegisteredOwner;
            }
            else
            {
                var withinWindow = retentionDays > 0 &&
                    (lastUsed > cutoff || facts.Any(fact => fact.UpdatedAt is null || fact.UpdatedAt > cutoff));
                var ownerLive = isOwnerRunning(owner);
                if (ownerLive) categories[root] = TerminalBuildRootRetainedBytes.Category.LiveOwner;
                else if (withinWindow) categories[root] = TerminalBuildRootRetainedBytes.Category.Window;
                candidates.Add((root, size, lastUsed, withinWindow, ownerLive));
            }
        }

        foreach (var candidate in candidates.OrderBy(value => value.LastUsed)
                     .ThenBy(value => value.Root, StringComparer.Ordinal))
        {
            var (root, size, _, withinWindow, ownerLive) = candidate;
            var sizeEligible = maxBytes is { } limit && remainingBytes > limit && size.Complete;
            var ageEligible = retentionDays > 0 && !withinWindow;
            // Keep skip-count precedence independent of retained-byte attribution.
            if (withinWindow && !sizeEligible)
                Skip(TerminalBuildRootSkipReason.WithinWindow);
            else if (ownerLive)
                Skip(TerminalBuildRootSkipReason.OwnerLive);
            else if (!ageEligible && !sizeEligible)
                continue;
            else if (attempts >= maxReclaims)
                Skip(TerminalBuildRootSkipReason.BudgetExhausted);
            else
            {
                attempts++;
                var renamed = Path.Combine(reclaimFolder, $"{Path.GetFileName(root)}-{timeProvider.GetUtcNow().UtcTicks}");
                try
                {
                    Directory.CreateDirectory(reclaimFolder);
                    Directory.Move(root, renamed);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Skip(TerminalBuildRootSkipReason.Held);
                    continue;
                }
                if (Remove(renamed, root))
                {
                    remainingBytes -= size.Bytes ?? 0;
                    removed.Add(root);
                }
            }
        }

        foreach (var (root, size) in roots)
            if (!removed.Contains(root))
                retained.Add(size, categories.TryGetValue(root, out var category) ? category : null);

        var reasons = string.Join(",", Enum.GetValues<TerminalBuildRootSkipReason>()
            .Where(reason => skipped.GetValueOrDefault(reason) > 0)
            .Select(reason => $"{Regex.Replace(reason.ToString(), "([a-z])([A-Z])", "$1-$2").ToLowerInvariant()}:{skipped[reason]}"));
        var summary = FormattableString.Invariant(
            $"SWEEP_BUILD_ROOT_RETENTION window-days={retentionDays} reclaimed={reclaimed.Count} bytes={bytes} skipped={skipped.Values.Sum()} reasons={(reasons.Length == 0 ? "none" : reasons)}");
        return new(reclaimed.Count, bytes, skipped, reclaimed,
            summary + " " + retained.FormatReceiptFields(maxBytes), retained);
    }

    private bool IsContainedDirectory(string path)
    {
        try
        {
            if (!storageRoot.ContainsPath(path)) return false;
            for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (string.Equals(current.FullName, storageRoot.RootPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    private bool TryReadLease(string root, string expectedGoalId, out int owner, out DateTimeOffset lastUsed)
    {
        owner = 0;
        lastUsed = default;
        try
        {
            var leaseFolder = Path.Combine(root, "lease");
            var leasePath = Path.Combine(leaseFolder, "lease.json");
            if (!IsContainedDirectory(leaseFolder) ||
                (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(leasePath));
            var lease = document.RootElement;
            return lease.TryGetProperty("goalId", out var id) && id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), expectedGoalId, StringComparison.OrdinalIgnoreCase) &&
                lease.TryGetProperty("ownerProcessId", out var pid) && pid.TryGetInt32(out owner) && owner > 0 &&
                lease.TryGetProperty("lastUsedAt", out var used) && used.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(used.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastUsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool IsOwnerProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
}
