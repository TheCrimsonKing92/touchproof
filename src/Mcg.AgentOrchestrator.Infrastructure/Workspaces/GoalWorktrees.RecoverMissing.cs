using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record GoalWorktreeRecoveryResult(
    GoalId GoalId, string CanonicalPath, string Branch, string? PreservedTip,
    bool Succeeded, bool Recreated, string Observation);

public static partial class GoalWorktrees
{
    // The caller retains goal lifecycle ownership through ordinary dispatch preparation.
    // Eligibility is re-read inside the same root lock used by creation and sweeping.
    public static GoalWorktreeRecoveryResult RecoverMissing(
        string executionDirectory, GoalId goalId, Func<string?> readOwnerRefusal,
        Action<GoalWorktreeRecoveryResult> recordReceipt, GoalWorktreeCleanupHooks? hooks = null) =>
        RecoverMissingCore(executionDirectory, goalId, readOwnerRefusal, recordReceipt, hooks, heldRootLock: null);

    // Initial creation and existing-branch recovery share the same root-lock lifetime.
    // The caller's authoritative lifecycle history controls whether initial creation is allowed.
    public static string PrepareForDispatch(
        string executionDirectory, GoalId goalId, bool allowInitialCreation,
        Func<string?> readOwnerRefusal, Action<GoalWorktreeRecoveryResult> recordReceipt,
        GoalWorktreeCleanupHooks? hooks = null)
    {
        var path = WorktreePath(executionDirectory, goalId);
        using var rootLock = GoalWorktreeRootLock.TryAcquire(Path.GetDirectoryName(path)!,
            TimeSpan.FromMilliseconds(GitCli.DefaultTimeoutMilliseconds));
        if (rootLock is null)
        {
            var failure = new GoalWorktreeRecoveryResult(goalId, path, BranchName(goalId), null, false, false, "creation/sweep lock unavailable");
            recordReceipt(failure);
            throw new InvalidOperationException(failure.Observation);
        }
        // Failed observations select the fail-closed recovery path; they never grant permission
        // to create a replacement branch. Recovery records their precise failure evidence.
        var initial = false;
        if (allowInitialCreation)
        {
            try
            {
                RequireUnredirectedRecoveryPath(path);
                var listing = GoalWorktreeRegistrationListing.Read(executionDirectory, hooks ?? new());
                RequireRecoveryListing(listing);
                var registration = RecoveryRegistration(listing, path, BranchName(goalId));
                var branch = GitCli.Run(executionDirectory, "show-ref", "--verify", "--quiet", $"refs/heads/{BranchName(goalId)}");
                initial = branch.ProcessStarted && !branch.DrainTimedOut && branch.ExitCode == 1 &&
                    registration is null && !Directory.Exists(path) && !File.Exists(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
        }
        if (initial && readOwnerRefusal() is null)
        {
            RequireUnredirectedRecoveryPath(path);
            if (Directory.Exists(path) || File.Exists(path))
                throw new InvalidOperationException("canonical path appeared before initial creation");
            RequireGitWorkTree(executionDirectory);
            EnsureWorktreeRootIgnored(executionDirectory);
            // Initial creation also preserves any state left by a failed add. The ordinary
            // Ensure API retains its existing cleanup contract for its other callers.
            RequireRecoveryGit(AddWorktree(executionDirectory, path, BranchName(goalId), branchExists: false), "initial worktree add");
            return path;
        }
        var recovered = RecoverMissingCore(executionDirectory, goalId, readOwnerRefusal, recordReceipt, hooks, rootLock);
        if (!recovered.Succeeded) throw new InvalidOperationException($"Goal worktree recovery refused: {recovered.Observation}");
        return recovered.CanonicalPath;
    }

    private static GoalWorktreeRecoveryResult RecoverMissingCore(
        string executionDirectory, GoalId goalId, Func<string?> readOwnerRefusal,
        Action<GoalWorktreeRecoveryResult> recordReceipt, GoalWorktreeCleanupHooks? hooks, GoalWorktreeRootLock? heldRootLock)
    {
        ArgumentNullException.ThrowIfNull(readOwnerRefusal);
        ArgumentNullException.ThrowIfNull(recordReceipt);
        var path = WorktreePath(executionDirectory, goalId);
        var branch = BranchName(goalId);
        string? tip = null;
        GoalWorktreeRecoveryResult Result(bool success, bool recreated, string observation) =>
            new(goalId, path, branch, tip, success, recreated, observation);
        GoalWorktreeRecoveryResult RecoverLocked()
        {
            using var rootLock = heldRootLock is null ? GoalWorktreeRootLock.TryAcquire(Path.GetDirectoryName(path)!,
                TimeSpan.FromMilliseconds(GitCli.DefaultTimeoutMilliseconds)) : null;
            if (rootLock is null && heldRootLock is null) return Result(false, false, "creation/sweep lock unavailable");
            if (readOwnerRefusal() is { } owner) return Result(false, false, owner);
            RequireUnredirectedRecoveryPath(path);
            var listing = GoalWorktreeRegistrationListing.Read(executionDirectory, hooks ?? new());
            RequireRecoveryListing(listing);
            var registration = RecoveryRegistration(listing, path, branch);
            tip = ReadRecoveryRevision(executionDirectory, $"refs/heads/{branch}^{{commit}}");
            if (Directory.Exists(path) || File.Exists(path))
            {
                // A concurrent completed recovery is reusable; an empty/partial checkout is not.
                VerifyRecoveredWorktree(executionDirectory, path, branch, tip, registration);
                var reused = Result(true, false, "reused verified existing worktree");
                recordReceipt(reused);
                return reused;
            }
            if (registration is { Locked: true }) throw new InvalidOperationException("canonical registration is locked");
            if (registration is not null && registration.Head != tip)
                throw new InvalidOperationException("stale registration HEAD differs from preserved branch tip");
            // Recheck immediately before the single exact-path mutation. No cleanup, prune,
            // branch creation, freshness merge, reset or retry is authorized here.
            RequireUnredirectedRecoveryPath(path);
            if (Directory.Exists(path) || File.Exists(path)) throw new InvalidOperationException("canonical path appeared before recovery");
            if (readOwnerRefusal() is { } changedOwner) return Result(false, false, changedOwner);
            if (ReadRecoveryRevision(executionDirectory, $"refs/heads/{branch}^{{commit}}") != tip)
                throw new InvalidOperationException("branch tip changed before recovery");
            RequireUnredirectedRecoveryPath(path);
            if (Directory.Exists(path) || File.Exists(path)) throw new InvalidOperationException("canonical path appeared before recovery");
            var add = AddWorktree(executionDirectory, path, branch, branchExists: true,
                recoverStaleRegistration: registration is not null);
            RequireRecoveryGit(add, "worktree add");
            var after = GoalWorktreeRegistrationListing.Read(executionDirectory, hooks ?? new());
            RequireRecoveryListing(after);
            RequireUnredirectedRecoveryPath(path);
            VerifyRecoveredWorktree(executionDirectory, path, branch, tip, RecoveryRegistration(after, path, branch));
            var recovered = Result(true, true, registration is null ? "absent registration restored" : "exact stale registration restored");
            // Persist success before releasing root protection or reporting readiness.
            recordReceipt(recovered);
            return recovered;
        }
        GoalWorktreeRecoveryResult result;
        try { result = RecoverLocked(); }
        catch (OperationCanceledException ex)
        {
            recordReceipt(Result(false, false, $"recovery interrupted: {ex.Message}"));
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            result = Result(false, false, ex.Message);
        }
        if (!result.Succeeded) recordReceipt(result);
        return result;
    }

    private static void RequireRecoveryListing(GoalWorktreeRegistrationListing listing)
    {
        if (!listing.Succeeded || !listing.Terminated)
            throw new InvalidOperationException($"registration listing unavailable: {listing.FailureReason ?? "unterminated listing"}");
        if (listing.Records.GroupBy(r => r.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new InvalidOperationException("duplicate worktree registrations");
    }

    private static GoalWorktreeRegistration? RecoveryRegistration(GoalWorktreeRegistrationListing listing, string path, string branch)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var canonical = GoalWorktreeLayout.NormalizePath(path);
        var matches = listing.Records.Where(r => string.Equals(r.Path, canonical, comparison)).ToArray();
        if (matches.Length > 1 || matches.Any(r => r.Branch != $"refs/heads/{branch}" || r.Bare))
            throw new InvalidOperationException("canonical path has a foreign or ambiguous registration");
        if (listing.Records.Any(r => r.Branch == $"refs/heads/{branch}" && !string.Equals(r.Path, canonical, comparison)))
            throw new InvalidOperationException("expected branch is registered at another path");
        return matches.SingleOrDefault();
    }

    private static void VerifyRecoveredWorktree(string repo, string path, string branch, string tip, GoalWorktreeRegistration? registration)
    {
        if (!File.Exists(Path.Combine(path, ".git")) || registration is null || registration.Locked || registration.Head != tip)
            throw new InvalidOperationException("existing directory or registration is incomplete, partial or locked");
        if ((File.GetAttributes(Path.Combine(path, ".git")) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("redirected worktree Git link");
        var expectedCommon = GitCli.Run(repo, "rev-parse", "--path-format=absolute", "--git-common-dir");
        var actualCommon = GitCli.Run(path, "rev-parse", "--path-format=absolute", "--git-common-dir");
        var topLevel = GitCli.Run(path, "rev-parse", "--show-toplevel");
        RequireRecoveryGit(expectedCommon, "repository common directory");
        RequireRecoveryGit(actualCommon, "worktree common directory");
        RequireRecoveryGit(topLevel, "worktree root");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(GoalWorktreeLayout.NormalizePath(expectedCommon.Output.Trim()), GoalWorktreeLayout.NormalizePath(actualCommon.Output.Trim()), comparison) ||
            !string.Equals(GoalWorktreeLayout.NormalizePath(topLevel.Output.Trim()), GoalWorktreeLayout.NormalizePath(path), comparison))
            throw new InvalidOperationException("worktree Git metadata belongs to a foreign repository or path");
        var symbolic = GitCli.Run(path, "symbolic-ref", "--quiet", "HEAD");
        RequireRecoveryGit(symbolic, "symbolic HEAD");
        if (symbolic.Output.Trim() != $"refs/heads/{branch}" || ReadRecoveryRevision(path, "HEAD^{commit}") != tip ||
            ReadRecoveryRevision(repo, $"refs/heads/{branch}^{{commit}}") != tip)
            throw new InvalidOperationException("recovered branch or HEAD differs from preserved branch tip");
    }

    private static string ReadRecoveryRevision(string repo, string revision)
    {
        var result = GitCli.Run(repo, "rev-parse", "--verify", revision);
        RequireRecoveryGit(result, revision);
        var commit = result.Output.Trim();
        if (commit.Length is not (40 or 64) || !commit.All(Uri.IsHexDigit))
            throw new InvalidOperationException($"unusable revision for {revision}");
        return commit;
    }

    private static void RequireRecoveryGit(GitCli.GitResult result, string operation)
    {
        if (!result.ProcessStarted || result.ExitCode != 0 || result.DrainTimedOut)
            throw new InvalidOperationException($"{operation} failed: exit={result.ExitCode} started={result.ProcessStarted} drain_timed_out={result.DrainTimedOut} {result.Error}");
    }

    private static void RequireUnredirectedRecoveryPath(string path)
    {
        for (string? component = Path.GetFullPath(path); component is not null; component = Path.GetDirectoryName(component))
        {
            try
            {
                var attributes = File.GetAttributes(component);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"redirected recovery path component: {component}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
