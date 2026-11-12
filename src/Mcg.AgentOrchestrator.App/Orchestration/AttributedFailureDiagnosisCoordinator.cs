using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the one bounded attempt and durable advisory outcome per goal/candidate/signature.
internal sealed class AttributedFailureDiagnosisCoordinator(
    Func<Goal, AttributedFailureRouteDecision, TimeSpan, CancellationToken, Task<AttributedFailureDiagnosisOutcome>> runInquiry,
    Action<GoalId, TaskId, string> recordNote,
    Func<TimeSpan, CancellationToken, Task>? waitForDeadline = null)
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private readonly object _gate = new();
    private readonly Dictionary<(GoalId, string, string), (CancellationToken Owner, Lazy<Task<AttributedFailureDiagnosisOutcome>> Attempt)> _attempts = [];

    internal static AttributedFailureDiagnosisCoordinator ForWorkspace(AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace, IReadOnlyList<AgentDefinition> agents) =>
        new(new AttributedFailureDiagnosisInquiry(new InquiryDispatcher(), agents,
                workspace.PromptDirectory, workspace.InquiryReceiptDirectory, workspace.GoalLifecycleEventsDirectory).RunAsync,
            (goal, task, note) => kernel.RecordTaskNote(goal, task, note));

    internal AttributedFailureRouteDecision Enrich(Goal goal, AttributedFailureRouteDecision route) =>
        EnrichAsync(goal, route).GetAwaiter().GetResult();

    internal async Task<AttributedFailureRouteDecision> EnrichAsync(Goal goal, AttributedFailureRouteDecision route,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!route.IsRoute || route.Diagnosis is not null) return route;
        if (route.Failure is null || route.Signature is null || route.Feedback is null)
            throw new InvalidOperationException("A diagnosis route requires failure, signature and feedback.");
        var key = (goal.Id, route.Failure.CandidateSha, route.Signature);
        Lazy<Task<AttributedFailureDiagnosisOutcome>> attempt;
        lock (_gate)
        {
            if (!_attempts.TryGetValue(key, out var existing) || existing.Owner.IsCancellationRequested)
                _attempts[key] = (cancellationToken, attempt = new(() => RunAsync(goal, route, cancellationToken)));
            else attempt = existing.Attempt;
        }
        AttributedFailureDiagnosisOutcome outcome;
        try { outcome = await attempt.Value.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            lock (_gate)
                if (_attempts.GetValueOrDefault(key).Attempt == attempt && attempt.Value.IsCanceled) _attempts.Remove(key);
            throw;
        }
        return route with { Diagnosis = outcome, Feedback = AttributedFailureDiagnosisNote.Insert(route.Feedback, outcome) };
    }

    private async Task<AttributedFailureDiagnosisOutcome> RunAsync(Goal goal, AttributedFailureRouteDecision route, CancellationToken enclosing)
    {
        var recorded = AttributedFailureDiagnosisRecord.Read(goal, route.Failure!.CandidateSha, route.Signature!);
        if (recorded is not null) return recorded;
        using var inquiryCancellation = CancellationTokenSource.CreateLinkedTokenSource(enclosing);
        using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(enclosing);
        var deadline = (waitForDeadline ?? Task.Delay)(DefaultTimeout, deadlineCancellation.Token);
        // Include synchronous command composition/queueing in the same end-to-end deadline.
        var inquiryToken = inquiryCancellation.Token;
        var inquiry = Task.Run(() => runInquiry(goal, route, DefaultTimeout, inquiryToken), CancellationToken.None);
        AttributedFailureDiagnosisOutcome outcome;
        try
        {
            var winner = await Task.WhenAny(inquiry, deadline).WaitAsync(enclosing).ConfigureAwait(false);
            enclosing.ThrowIfCancellationRequested();
            if (winner == deadline)
            {
                _ = inquiryCancellation.CancelAsync();
                outcome = AttributedFailureDiagnosisOutcome.Unavailable(route, "timed-out", "inquiry timed out after 120 seconds");
            }
            else outcome = await inquiry.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!enclosing.IsCancellationRequested)
        {
            outcome = AttributedFailureDiagnosisOutcome.Unavailable(route, "failed", "inquiry cancelled");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = AttributedFailureDiagnosisOutcome.Unavailable(route, "failed", "inquiry failed: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            _ = deadlineCancellation.CancelAsync();
            // A late result is observed only for fault cleanup; it can never replace the recorded outcome.
            _ = inquiry.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        enclosing.ThrowIfCancellationRequested();
        recordNote(goal.Id, route.DeveloperTaskId!, AttributedFailureDiagnosisRecord.Format(outcome));
        return outcome;
    }
}
