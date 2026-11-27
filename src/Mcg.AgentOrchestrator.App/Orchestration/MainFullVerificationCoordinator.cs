using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class MainFullVerificationCoordinator(
    MainFullVerificationStore store, ICollaborationItemStore items, int intervalSeconds,
    Func<string?> mainTip, Func<string, CancellationToken, MainFullVerificationRun?> reserve,
    Func<DateTimeOffset> clock, Action<string> progress) : IDisposable
{
    internal const string HoldEvent = "MAIN_FULL_VERIFICATION_HOLD";
    internal const string Event = "MAIN_FULL_VERIFICATION";
    private const int MaxHoldLines = 10;
    private readonly CancellationTokenSource _stop = new();
    private MainFullVerificationRecord? _active;
    private Task<(MainFullVerificationResult Result, DateTimeOffset CompletedAt)>? _execution;
    private bool _initialized;

    internal string? ActiveRunId => _active is { } active ? MainFullVerificationRunner.RunId(active.Commit) : null;
    internal Task? ActiveExecution => _execution;

    internal static MainFullVerificationCoordinator? Create(OrchestratorWorkspace? workspace, ConductorDriver driver,
        Func<DateTimeOffset> clock, Action<string> progress)
    {
        if (workspace is null || driver._cohortWorkspace is null || driver._cohortAcceptanceVerifier is not { } verifier)
            return null;

        var store = new MainFullVerificationStore(workspace.RunEventStorePath);
        var runner = new MainFullVerificationRunner(workspace, verifier, driver.ParallelAcceptanceAttemptCoordinator,
            new SelectedExecutionBackstopStore(new SqliteRunEventStore(workspace.RunEventStorePath, ensureSchema: false)));
        var interval = MainFullVerificationSettings.ReadIntervalSeconds(
            Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json"), progress);
        progress($"{Event} result=configured intervalSeconds={interval}");
        return new(store, CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory), interval,
            () => ReadMainTip(workspace), runner.TryReserve, clock, progress);
    }

    private static string? ReadMainTip(OrchestratorWorkspace workspace)
    {
        var result = GitCli.Run(workspace.ExecutionDirectory, "rev-parse", "--verify", $"refs/heads/{workspace.IntegrationBranch}");
        return result.Succeeded && !result.DrainTimedOut ? result.Output.Trim() : null;
    }

    internal MainFullVerificationScheduler.Decision Service(int waitingGoalGates, Func<bool> capacityAdmitted)
    {
        Poll();
        var now = clock();
        var previous = store.Latest("admitted")?.AdmittedAt;
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        if (_active is not null || (previous is { } admitted && now - admitted < interval))
            return MainFullVerificationScheduler.Decision.NotDue;

        var commit = mainTip();
        var alreadyRequested = commit is not null && store.Contains(commit, "admitted");
        var decision = MainFullVerificationScheduler.Decide(commit, alreadyRequested, previous, now, interval,
            outstanding: false, waitingGoalGates, capacityAdmitted: true);
        if (decision != MainFullVerificationScheduler.Decision.Admit) return decision;
        if (!capacityAdmitted() || reserve(commit!, _stop.Token) is not { } run)
            return MainFullVerificationScheduler.Decision.Deferred;

        try
        {
            var passed = store.Latest("completed", "passed");
            var passedWithUnconfirmed = store.Latest("completed", MainFullVerificationResult.PassedWithUnconfirmed);
            var lastPassed = (passedWithUnconfirmed?.CompletedAt > passed?.CompletedAt
                ? passedWithUnconfirmed : passed ?? passedWithUnconfirmed)?.Commit;
            var record = new MainFullVerificationRecord(commit!, now, intervalSeconds);
            store.Write("admitted", record, now);
            var started = clock();
            record = record with { StartedAt = started };
            store.Write("started", record, started);
            var token = _stop.Token;
            _execution = Task.Run(() => (Execute(run, lastPassed, token), clock()));
            _active = record;
        }
        catch
        {
            run.Lease?.Dispose();
            _initialized = false;
            throw;
        }

        progress($"{Event} result=started commit={commit} intervalSeconds={intervalSeconds}");
        return decision;
    }

    internal void Poll()
    {
        if (!_initialized)
        {
            if (store.Latest("admitted") is { } pending && !store.Contains(pending.Commit, "completed"))
                Complete(store.Latest("started") is { } started && started.Commit == pending.Commit ? started : pending,
                    new("interrupted", "conductor-generation-ended"), clock());
            _initialized = true;
        }

        if (_execution is { IsCompleted: true } execution && _active is { } active)
        {
            var (result, completedAt) = execution.GetAwaiter().GetResult();
            Complete(active, result, completedAt);
            _execution = null;
            _active = null;
        }

        if (store.Latest("completed") is { } latest)
            PublishHold(latest);
    }

    private static MainFullVerificationResult Execute(MainFullVerificationRun run, string? lastPassed, CancellationToken token)
    {
        try
        {
            using var lease = run.Lease;
            token.ThrowIfCancellationRequested();
            var result = run.Execute(lastPassed, token);
            return token.IsCancellationRequested ? new("interrupted", "conductor-stopped") : result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new("interrupted", "conductor-stopped");
        }
        catch (Exception ex)
        {
            return new("incomplete", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Complete(MainFullVerificationRecord active, MainFullVerificationResult result, DateTimeOffset now)
    {
        if (store.Contains(active.Commit, "completed")) return;
        var duration = Math.Max(0, (now - (active.StartedAt ?? active.AdmittedAt)).TotalSeconds);
        var completed = active with { CompletedAt = now, DurationSeconds = duration, Result = result };
        store.Write("completed", completed, now);
        var count = result.Failures?.Count ?? 0;
        var unconfirmed = result.Unconfirmed is { Count: > 0 } names
            ? $" unconfirmedTests={OneLine(string.Join(",", names))}" : "";
        progress(FormattableString.Invariant(
            $"{Event} result={result.Outcome} commit={active.Commit} durationSeconds={duration:0.###} failingTests={count} truncated={Math.Max(0, count - MaxHoldLines)} detail={OneLine(result.Detail ?? "none")}{unconfirmed}"));
    }

    private void PublishHold(MainFullVerificationRecord record)
    {
        // Confirmation fills Unconfirmed even when all reruns fail. Older records lack that evidence.
        if (record.Result is not { Outcome: "failed", Unconfirmed: not null, Failures: { Count: > 0 } failures })
            return;
        if (!store.Contains(record.Commit, "hold-published"))
        {
            string? tip;
            try { tip = mainTip(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { tip = null; }
            var reason = tip is not null && !string.Equals(tip, record.Commit, StringComparison.OrdinalIgnoreCase)
                ? "tip-moved" : "confirmed-by-rerun";
            foreach (var failure in failures.Take(MaxHoldLines))
                progress($"{HoldEvent} commit={record.Commit} test={OneLine(failure.Test)} landing={failure.Landing ?? "undetermined"} landingGoal={failure.Goal ?? "none"} revert=withheld reason={reason}");
            store.Write("hold-published", record, clock());
        }

        if (store.Contains(record.Commit, "item-raised")) return;
        var correlation = $"main-full-verification-failure:{record.Commit}";
        try
        {
            if (!items.ListAsync().GetAwaiter().GetResult().Any(item => item.CorrelationKey == correlation))
                items.RaiseAsync(CollaborationItemType.Verify, null, $"Main full verification failed at {record.Commit}",
                    JsonSerializer.Serialize(record), correlation).GetAwaiter().GetResult();
            store.Write("item-raised", record, clock());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            progress($"{Event} result=item-pending commit={record.Commit} detail={OneLine(ex.Message)}");
        }
    }

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    public void Dispose()
    {
        try
        {
            if (_active is not null)
            {
                Poll();
                if (_active is { } active)
                {
                    _stop.Cancel();
                    Complete(active, new("interrupted", "conductor-stopped"), clock());
                }
            }
        }
        finally
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }
}
