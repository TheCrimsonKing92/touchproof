namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class WorktreeFixtureScratchCleaner(
    TimeSpan? budget = null,
    Func<DateTimeOffset>? utcNow = null,
    Func<int, ProcessInspectionStatus>? inspect = null,
    Action<string, bool>? deleteEntry = null,
    Func<TimeSpan, CancellationToken, Task>? waitForBudget = null)
{
    internal Task<WorktreeFixtureScratchCleanupResult>? Completion { get; private set; }
    private static readonly string[] FixtureRoots =
        ["mcg-wt", "mcg-git-helper-diag", "mcg-long-wt", "invoke-repo-script-tests"];

    internal WorktreeFixtureScratchCleanupResult Clean(string worktree)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var work = Completion = Task.Run(() => CleanCore(worktree, token));
        var deadline = (waitForBudget ?? ((duration, ct) => Task.Delay(duration, ct)))(
            budget ?? TimeSpan.FromSeconds(5), token);
        try
        {
            if (Task.WhenAny(work, deadline).GetAwaiter().GetResult() == work)
                return work.GetAwaiter().GetResult();
            // The worker catches failures and checks cancellation before every filesystem
            // operation. An already-issued operation can finish after this return.
            return new(true, true, 0, 0);
        }
        finally { cancellation.Cancel(); }
    }

    private WorktreeFixtureScratchCleanupResult CleanCore(string worktree, CancellationToken token)
    {
        var found = false;
        var removed = 0;
        var retained = 0;
        try
        {
            var root = Path.GetFullPath(worktree);
            // Linked worktrees have a git file. The home repository has a directory.
            if (!File.Exists(Path.Combine(root, ".git"))) return new(false, false, 0, 0);
            var scratch = Path.Combine(root, ".scratch");
            if (!Directory.Exists(scratch)) return new(false, false, 0, 0);
            using var scratchLease = new FixtureCleanupDirectoryLease(scratch, ancestors: true);
            var cutoff = (utcNow ?? (() => DateTimeOffset.UtcNow))() - TimeSpan.FromMinutes(10);
            foreach (var name in FixtureRoots)
            {
                token.ThrowIfCancellationRequested();
                var fixture = Path.Combine(scratch, name);
                if (!Path.Exists(fixture)) continue;
                found = true;
                try
                {
                    if (IsLink(fixture)) { Delete(fixture); continue; }
                    if (!Directory.Exists(fixture)) { retained++; continue; }
                    using (var fixtureLease = new FixtureCleanupDirectoryLease(fixture))
                    {
                        var owners = TempRootJanitor.ListOwnedFixtureRoots([fixture])
                            .ToDictionary(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase);
                        foreach (var child in Directory.EnumerateFileSystemEntries(fixture))
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                owners.TryGetValue(child, out var owned);
                                var canRemove = owned is not null
                                    ? (inspect ?? Inspect)(owned.ProcessId) == ProcessInspectionStatus.Exited
                                    : IsQuiet(child);
                                if (canRemove) Delete(child); else retained++;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch { retained++; }
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    if (!Directory.EnumerateFileSystemEntries(fixture).Any()) Delete(fixture);
                }
                catch (OperationCanceledException) { throw; }
                catch { retained++; }
            }

            bool IsQuiet(string path, int depth = 0)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 128) return false;
                if (IsLink(path)) return true; // Read/delete the link, never its target.
                if (File.GetLastWriteTimeUtc(path) > cutoff.UtcDateTime) return false;
                if (!Directory.Exists(path)) return true;
                using var lease = new FixtureCleanupDirectoryLease(path);
                return Directory.EnumerateFileSystemEntries(path).All(child => IsQuiet(child, depth + 1));
            }

            void Delete(string path, int depth = 0)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 128) throw new IOException("Fixture cleanup depth limit exceeded.");
                var attributes = File.GetAttributes(path);
                var directory = attributes.HasFlag(FileAttributes.Directory);
                var link = attributes.HasFlag(FileAttributes.ReparsePoint);
                if (directory && !link)
                {
                    using var lease = new FixtureCleanupDirectoryLease(path);
                    foreach (var child in Directory.EnumerateFileSystemEntries(path)) Delete(child, depth + 1);
                    if (attributes.HasFlag(FileAttributes.ReadOnly))
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
                token.ThrowIfCancellationRequested();
                // Never clear attributes through a link (which can affect its target).
                if (!directory && !link && attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    using var lease = new FixtureCleanupDirectoryLease(path);
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
                token.ThrowIfCancellationRequested();
                if (deleteEntry is not null) deleteEntry(path, directory);
                else if (directory) Directory.Delete(path, recursive: false);
                else File.Delete(path);
                removed++;
            }
        }
        catch (OperationCanceledException) { }
        catch { retained++; }
        return new(found, token.IsCancellationRequested, removed, retained);
    }

    private static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static ProcessInspectionStatus Inspect(int processId)
    {
        var result = WindowsNativeProcessInspection.Read([processId]);
        return result.Records.TryGetValue(processId, out var owner)
            ? owner.Status : ProcessInspectionStatus.NativeFailure;
    }
}
