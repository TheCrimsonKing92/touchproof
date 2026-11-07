using System.Security;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum WorktreeDescendantIntegrityScanOutcome
{
    Prepared,
    MediumDescendant,
    BudgetExceeded,
    Incomplete
}

internal sealed record WorktreeDescendantIntegrityScanResult(
    WorktreeDescendantIntegrityScanOutcome Outcome,
    string? Path = null);

// A receipt can be reused only after its writable descendants have been verified.
internal static class WorktreeDescendantIntegrityScan
{
    internal const int MaxDescendantEntries = 4096;

    internal static WorktreeDescendantIntegrityScanResult Scan(
        string root, IWorkerIntegrityLabeler labeler, int maxEntries = MaxDescendantEntries)
    {
        try
        {
            var pending = new Stack<(DirectoryInfo Directory, bool IsRoot)>();
            pending.Push((new DirectoryInfo(root), true));
            var entries = 0;
            while (pending.TryPop(out var parent))
            {
                foreach (var child in parent.Directory.EnumerateFileSystemInfos())
                {
                    // Count files, links and exclusions too, bounding enumeration as well as queries.
                    if (++entries > maxEntries)
                        return new(WorktreeDescendantIntegrityScanOutcome.BudgetExceeded, child.FullName);

                    var attributes = child.Attributes;
                    // Keep these top-level exclusions in sync with AcceptanceGateIntegrityNormalizer.
                    if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                        (parent.IsRoot &&
                         (child.Name.Equals(".mcg-sandbox", StringComparison.OrdinalIgnoreCase) ||
                          child.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))))
                        continue;

                    if ((attributes & FileAttributes.Directory) == 0)
                        continue;

                    var state = labeler.Query(child.FullName);
                    if (state.Medium || state.Unlabeled)
                        return new(WorktreeDescendantIntegrityScanOutcome.MediumDescendant, child.FullName);
                    if (!state.Exists || !state.Low || state.NativeQueryError is not null)
                        return new(WorktreeDescendantIntegrityScanOutcome.Incomplete, child.FullName);

                    pending.Push(((DirectoryInfo)child, false));
                }
            }

            return new(WorktreeDescendantIntegrityScanOutcome.Prepared);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            // An incomplete check cannot justify a receipt hit; let the existing repair path run.
            return new(WorktreeDescendantIntegrityScanOutcome.Incomplete);
        }
    }
}
