using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class PreTesterTestImpactPlanAttemptCoordinator(
    string root, string executionDirectory,
    Func<string, Guid, SpawnProcessIdentity?>? launch = null,
    Func<SpawnProcessIdentity, SpawnTrackedProcessStatus>? classifyHolder = null)
{
    internal const string OwnedProcessSubcommandName = "__pre-tester-test-impact-plan-attempt";
    private const string MetadataFile = "attempt.json";
    private const string ResultFile = "result.json";

    internal sealed record AttemptKey(TickTestImpactPlanMemo.Candidate Candidate, string? SummaryJson, string? ReversePathsJson)
    {
        internal string Hash => SoloBaselineRepairStore.Key(
            Candidate.WorktreePath, Candidate.Head, Candidate.MainRevision, Candidate.MergeBase, SummaryJson, ReversePathsJson);
    }

    private sealed record Attempt(Guid LaunchId, AttemptKey Key, SpawnProcessIdentity? Owner = null,
        string Outcome = "running", string? Detail = null);

    internal enum State { Pending, Completed, Failed }
    internal sealed record Observation(State Status, string Detail, StoredResult? Result = null);

    internal sealed record StoredResult(AttemptKey Key, PlanningRead<StoredPlan>? Plan,
        PlanningRead<RepositoryRecheckTestSelection>? ReverseSelection)
    {
        internal bool Matches(AttemptKey key) => Key == key &&
            (key.SummaryJson is null ? Plan is null : Plan is { Error: not null, Value: null } ||
                Plan is { Error: null, Value: { } plan } && plan.IsComplete()) &&
            (key.ReversePathsJson is null ? ReverseSelection is null : ReverseSelection is { Error: not null, Value: null } ||
                ReverseSelection is { Error: null, Value.TestClasses: not null });
    }

    internal sealed record StoredPlan(RepositoryTestImpactPlan Plan, IReadOnlyList<IReadOnlyList<string>> ChangedTestClasses)
    {
        internal static StoredPlan Capture(RepositoryTestImpactPlan plan) =>
            new(plan, plan.Checks.Select(check => check.ChangedTestClasses).ToArray());

        internal bool IsComplete() => Plan?.Checks is { } checks && ChangedTestClasses?.Count == checks.Count &&
            checks.All(check => check?.Command is not null) && ChangedTestClasses.All(classes => classes is not null);

        internal RepositoryTestImpactPlan Restore() => Plan with
        {
            Checks = Plan.Checks.Select((check, index) => check with { ChangedTestClasses = ChangedTestClasses[index] }).ToArray()
        };
    }

    internal sealed record PlanningRead<T>(T? Value, PlanningError? Error) where T : class
    {
        internal T Read() => Value ?? throw Error!.ToException();

        internal static PlanningRead<T> Capture(Func<T> compute)
        {
            try { return new(compute(), null); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return new(null, new(ex.GetType().Name, ex.Message));
            }
        }
    }

    internal sealed record PlanningError(string Kind, string Message)
    {
        internal Exception ToException() => Kind switch
        {
            nameof(InvalidDataException) => new InvalidDataException(Message),
            nameof(UnauthorizedAccessException) => new UnauthorizedAccessException(Message),
            nameof(FileNotFoundException) => new FileNotFoundException(Message),
            nameof(DirectoryNotFoundException) => new DirectoryNotFoundException(Message),
            nameof(PathTooLongException) => new PathTooLongException(Message),
            _ => new IOException(Message)
        };
    }

    internal Observation Observe(AttemptKey key)
    {
        var directory = Path.Combine(root, key.Hash);
        try
        {
            using var writer = StorageRetentionMaintenance.TryAcquireAttemptWriterLease(directory, TimeSpan.Zero);
            if (writer is null)
                return new(State.Pending, "running");

            var path = Path.Combine(directory, MetadataFile);
            var attempt = Read<Attempt>(path);
            if (attempt is null)
                return Start(path, key);
            if (attempt.Key != key)
                return new(State.Failed, "attempt-key-mismatch");
            if (attempt.Outcome == "completed")
            {
                try
                {
                    var result = Read<StoredResult>(Path.Combine(directory, ResultFile));
                    if (result?.Matches(key) == true)
                        return new(State.Completed, "completed", result);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    return Fail(path, attempt, "result-unusable", ex.Message);
                }
                return Fail(path, attempt, "result-unusable", "Planning result is missing, incomplete, or belongs to another request.");
            }
            if (attempt.Outcome != "running")
                return Failed(attempt);
            if (attempt.Owner is { } owner && (classifyHolder?.Invoke(owner) ??
                AcceptanceHolderIdentity.Classify(owner.ProcessId, owner.StartedAt, owner.ImagePath))
                != SpawnTrackedProcessStatus.DeadOrRecycled)
                return new(State.Pending, "running");

            return Fail(path, attempt, "owner-dead",
                attempt.Owner is { } dead ? $"Planning process {dead.ProcessId} exited without a result." :
                    "The launch did not record an owner.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(State.Failed, $"attempt-unusable: {ex.Message}");
        }
    }

    private Observation Start(string path, AttemptKey key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var attempt = new Attempt(Guid.NewGuid(), key);
        Save(path, attempt);
        try
        {
            var owner = (launch ?? LaunchOwnedProcess)(path, attempt.LaunchId);
            if (owner is null)
                return Fail(path, attempt, "launch-failed", "Could not capture the planning process identity.");
            Save(path, attempt with { Owner = owner });
            return new(State.Pending, "started");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return Fail(path, attempt, "launch-failed", ex.Message);
        }
    }

    internal static int RunOwnedProcess(string metadataPath, Guid launchId)
    {
        try
        {
            return RunAttempt(metadataPath, launchId, key => ComputeWithLogs(Path.GetDirectoryName(metadataPath)!, key),
                AcceptanceHolderIdentity.Capture(Environment.ProcessId));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static StoredResult ComputeWithLogs(string directory, AttemptKey key)
    {
        using var stdout = File.AppendText(Path.Combine(directory, "stdout.log"));
        using var stderr = File.AppendText(Path.Combine(directory, "stderr.log"));
        var originalOut = Console.Out;
        var originalError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try { return Compute(key); }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    internal static int RunAttempt(string metadataPath, Guid launchId, Func<AttemptKey, StoredResult> compute,
        SpawnProcessIdentity? owner = null)
    {
        var directory = Path.GetDirectoryName(metadataPath)!;
        using var writer = StorageRetentionMaintenance.AcquireAttemptWriterLease(directory);
        if (Read<Attempt>(metadataPath) is not { Outcome: "running" } attempt || attempt.LaunchId != launchId ||
            Path.GetFileName(directory) != attempt.Key.Hash)
            return 1;

        if (owner is not null)
        {
            attempt = attempt with { Owner = owner };
            Save(metadataPath, attempt);
        }

        try
        {
            var result = compute(attempt.Key);
            if (!result.Matches(attempt.Key))
                throw new InvalidDataException("Planning returned an incomplete or mismatched result.");
            Save(Path.Combine(directory, ResultFile), result);
            Save(metadataPath, attempt with { Outcome = "completed" });
            return 0;
        }
        catch (Exception ex)
        {
            Fail(metadataPath, attempt, "failed", ex.Message);
            return 1;
        }
    }

    internal static StoredResult Compute(AttemptKey key,
        Func<RepositoryChangeSummary, string, RepositoryTestImpactPlan>? planner = null,
        Func<string, IReadOnlyList<string>, RepositoryRecheckTestSelection>? selector = null)
    {
        var summary = key.SummaryJson is { } planInput ? JsonSerializer.Deserialize<RepositoryChangeSummary>(planInput) : null;
        var paths = key.ReversePathsJson is { } reverseInput ? JsonSerializer.Deserialize<string[]>(reverseInput) : null;
        var candidate = key.Candidate;
        var inputs = (summary?.Files.Select(file => file.Path) ?? []).Concat(paths ?? []).ToArray();

        RequireReusableCandidate();
        // Keep expected read failures as inputs to the guard's existing degradation paths.
        var plan = summary is null ? null : PlanningRead<StoredPlan>.Capture(
            () => StoredPlan.Capture((planner ?? RepositoryTestImpactPlanner.Plan)(summary, candidate.WorktreePath)));
        var selection = paths is null ? null : PlanningRead<RepositoryRecheckTestSelection>.Capture(
            () => (selector ?? RepositoryRecheckTestSelection.Read)(candidate.WorktreePath, paths));
        RequireReusableCandidate();
        return new(key, plan, selection);

        void RequireReusableCandidate()
        {
            if (!TickTestImpactPlanMemo.CanReuse(candidate, inputs))
                throw new InvalidOperationException($"Planning inputs changed or could not be verified for {candidate.Head}.");
        }
    }

    private SpawnProcessIdentity? LaunchOwnedProcess(string metadataPath, Guid launchId)
    {
        var executable = Environment.ProcessPath ?? "dotnet";
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = executionDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(PreTesterTestImpactPlanAttemptCoordinator).Assembly.Location);
        info.ArgumentList.Add(OwnedProcessSubcommandName);
        info.ArgumentList.Add(metadataPath);
        info.ArgumentList.Add(launchId.ToString("D"));
        CrosswalkExecutableComposition.PropagateDataRoot(info.Environment);

        var process = HandleListProcessLauncher.StartSuppressed(info);
        try { return AcceptanceHolderIdentity.Capture(process.Id); }
        finally { _ = DrainAndReleaseAsync(process); }
    }

    private static async Task DrainAndReleaseAsync(HandleListLaunchedProcess process)
    {
        using (process)
        {
            try
            {
                process.StandardInput.Close();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
            {
                // Terminal status is published under the child writer lease.
            }
        }
    }

    private static Observation Fail(string path, Attempt attempt, string outcome, string detail)
    {
        var failed = attempt with { Outcome = outcome, Detail = detail };
        Save(path, failed);
        return Failed(failed);
    }

    private static Observation Failed(Attempt attempt) => new(State.Failed, $"{attempt.Outcome}: {attempt.Detail}");

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream) ?? throw new JsonException("Attempt artifact contains no data.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void Save<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value));
        File.Move(temp, path, overwrite: true);
    }
}
