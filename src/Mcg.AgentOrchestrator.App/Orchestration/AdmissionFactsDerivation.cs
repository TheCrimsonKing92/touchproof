using System.Collections.Concurrent;
using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One instance is owned by one repository's conductor. No gate slot or git work under the queue lock.
internal sealed class AdmissionFactsDerivation(
    Func<IReadOnlyList<AdmissionFactsInputs>, CancellationToken, IReadOnlyList<AdmissionFacts>> derive,
    Func<DateTimeOffset>? utcNow = null, Func<int>? mergeLaunches = null)
{
    private sealed record Pending(AdmissionFactsInputs Inputs, DateTimeOffset Since, DateTimeOffset RetryAt);
    private readonly object _gate = new();
    private readonly Dictionary<GoalId, Pending> _pending = [];
    private readonly Dictionary<GoalId, AdmissionFactsInputs> _desired = [];
    private readonly ConcurrentDictionary<GoalId, AdmissionFacts> _finished = new();
    private readonly ConcurrentQueue<string> _reports = new();
    private readonly Func<DateTimeOffset> _now = utcNow ?? (() => DateTimeOffset.UtcNow);
    private CancellationTokenSource _cancellation = new();
    private Task _worker = Task.CompletedTask;
    private bool _running, _stopped;
    internal Action<AdmissionFactsInputs, CancellationToken>? BeforeDerive { get; set; }
    internal int PendingCount { get { lock (_gate) return _pending.Count; } }
    internal Task Completion { get { lock (_gate) return _worker; } }

    internal AdmissionFacts? Read(AdmissionFactsInputs inputs) =>
        _finished.TryGetValue(inputs.GoalId, out var facts) && facts.Inputs == inputs ? facts : null;

    internal TimeSpan Request(AdmissionFactsInputs inputs)
    {
        lock (_gate)
        {
            _desired[inputs.GoalId] = inputs;
            if (Read(inputs) is not null)
            {
                _pending.Remove(inputs.GoalId);
                return TimeSpan.Zero;
            }
            if (!_pending.TryGetValue(inputs.GoalId, out var item) || item.Inputs != inputs)
                _pending[inputs.GoalId] = item = new(inputs, _now(), DateTimeOffset.MinValue);
            return _now() - item.Since;
        }
    }

    internal void Retain(IReadOnlySet<GoalId> ids)
    {
        lock (_gate)
            foreach (var id in _desired.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                _desired.Remove(id);
                _pending.Remove(id);
                _finished.TryRemove(id, out _);
            }
    }

    internal void Start()
    {
        lock (_gate)
        {
            _stopped = false;
            if (_cancellation.IsCancellationRequested) _cancellation = new();
            if (_running || !_pending.Values.Any(item => item.RetryAt <= _now())) return;
            _running = true;
            // Suppress the tick's ambient telemetry/evidence scopes on the background worker.
            using (ExecutionContext.SuppressFlow()) _worker = Task.Run(Run);
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _cancellation.Cancel();
        }
    }

    internal IEnumerable<string> DrainReports()
    {
        while (_reports.TryDequeue(out var report)) yield return report;
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                AdmissionFactsInputs[] batch;
                CancellationToken token;
                lock (_gate)
                {
                    if (_stopped) return;
                    token = _cancellation.Token;
                    batch = _pending.Values.Where(item => item.RetryAt <= _now()).Select(item => item.Inputs).ToArray();
                    if (batch.Length == 0) return;
                    // Keep active inputs in the one-entry map to coalesce duplicates and preserve age.
                    foreach (var input in batch) _pending[input.GoalId] = _pending[input.GoalId] with { RetryAt = DateTimeOffset.MaxValue };
                }
                var timer = Stopwatch.StartNew();
                var derivationStarted = false;
                try
                {
                    foreach (var input in batch) BeforeDerive?.Invoke(input, token);
                    token.ThrowIfCancellationRequested();
                    derivationStarted = true;
                    var facts = derive(batch, token);
                    lock (_gate)
                    {
                        foreach (var result in facts)
                            if (!token.IsCancellationRequested && _desired.TryGetValue(result.Inputs.GoalId, out var desired) && desired == result.Inputs)
                            {
                                _finished[result.Inputs.GoalId] = result;
                                _pending.Remove(result.Inputs.GoalId);
                            }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    _reports.Enqueue($"ADMISSION_FACTS_FAILED error={exception.GetType().Name} detail={exception.Message.ReplaceLineEndings(" ")}");
                }
                finally
                {
                    lock (_gate)
                        foreach (var input in batch)
                            if (_pending.TryGetValue(input.GoalId, out var item) && item.Inputs == input)
                                _pending[input.GoalId] = item with { RetryAt = token.IsCancellationRequested ? DateTimeOffset.MinValue : _now().AddSeconds(30) };
                    _reports.Enqueue(FormattableString.Invariant($"ADMISSION_FACTS_BATCH candidates={batch.Length} elapsed_ms={timer.ElapsedMilliseconds} merge_tree_launches={(derivationStarted ? mergeLaunches?.Invoke() ?? 0 : 0)}"));
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                while (_reports.Count > 64) _reports.TryDequeue(out _);
                if (!_stopped && _pending.Values.Any(item => item.RetryAt <= _now())) Start();
            }
        }
    }
}
