using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerQuestionReadModel(
    IOrchestratorStateQueries state, string orchestratorDirectory,
    OwnerConsoleRefreshGoalSnapshots? snapshots = null, Func<string?>? changeMarker = null,
    Func<string?>? stateMarker = null) : IOwnerQuestionSource
{
    private readonly OwnerQuestionReadModelState _stateReader = new(state, snapshots);
    private OwnerQuestionSnapshot? _snapshot;
    private string? _version;
    private long _revision;
    private OwnerQuestionStateSnapshot? _stateQuestions;
    private string? _stateVersion;
    private long _stateRevision = -1;
    internal (int Goals, int Candidates, int TerminalHolds) RetainedState =>
        (_stateQuestions?.Goals.Count ?? 0, _stateQuestions?.Candidates.Count ?? 0, _stateReader.TerminalHoldCount);

    public void Invalidate() => Interlocked.Increment(ref _revision);
    private long _acceptedRevision = -1;

    public async Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken) =>
        (await ReadAsync(cancellationToken)).Live;

    public async Task<OwnerQuestionSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var revision = Interlocked.Read(ref _revision);
        var version = changeMarker?.Invoke();
        if (version is not null && version == _version && revision == _acceptedRevision && _snapshot is not null)
            return _snapshot;
        _version = null;
        _snapshot = null;
        var stateVersion = stateMarker?.Invoke();
        var stateQuestions = _stateQuestions;
        if (stateVersion is null || stateVersion != _stateVersion || revision != _stateRevision || stateQuestions is null)
        {
            _stateQuestions = null;
            _stateVersion = null;
            // UpdatedAt alone cannot validate request rows or a write retaining its timestamp.
            if (changeMarker is not null || stateMarker is not null)
                _stateReader.ClearTerminalHolds();
            stateQuestions = await _stateReader.ReadAsync(
                await state.ListGoalBoardRowsAsync(new(null, 0, null), cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (stateVersion is not null && stateVersion == stateMarker?.Invoke() && revision == Interlocked.Read(ref _revision))
            {
                _stateQuestions = stateQuestions;
                _stateVersion = stateVersion;
                _stateRevision = revision;
            }
        }
        var snapshot = await ReadCollaborationAsync(stateQuestions, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (version is not null && version == changeMarker?.Invoke() && revision == Interlocked.Read(ref _revision))
        {
            _snapshot = snapshot;
            _version = version;
            _acceptedRevision = revision;
        }
        return snapshot;
    }

    internal async Task<OwnerQuestionSnapshot> ReadAsync(
        IReadOnlyList<GoalSummary> metadata, CancellationToken cancellationToken) =>
        await ReadCollaborationAsync(await _stateReader.ReadAsync(metadata, cancellationToken), cancellationToken);

    private async Task<OwnerQuestionSnapshot> ReadCollaborationAsync(OwnerQuestionStateSnapshot stateQuestions,
        CancellationToken cancellationToken)
    {
        var goals = stateQuestions.Goals;
        var awaitingVerificationGoals = stateQuestions.AwaitingVerification;
        var candidates = new List<OwnerQuestionCandidate>(stateQuestions.Candidates);
        var path = Path.Combine(orchestratorDirectory, "collaboration-items.db");
        if (File.Exists(path))
        {
            var store = CollaborationItemStore.OpenExisting(orchestratorDirectory);
            var items = await store.GetAttentionQueueAsync(cancellationToken);
            foreach (var item in items.Where(item => !CollaborationItemLifecycle.IsTerminal(item.Status)))
            {
                if (item.GoalId is not null &&
                    item.CorrelationKey?.StartsWith(PassedGateHoldAttention.Prefix, StringComparison.Ordinal) == true)
                {
                    if (goals.GetValueOrDefault(item.GoalId) == GoalStatus.Verified)
                        candidates.Add(new OwnerQuestionCandidate(new OwnerQuestion(item.Id, item.GoalId,
                            OwnerQuestionKind.PassedGateHold, item.Body, CanAnswer: false), item.RaisedAt));
                    continue;
                }
                if (item.GoalId is not null &&
                    item.CorrelationKey?.StartsWith(AwaitingVerificationGateAttention.Prefix, StringComparison.Ordinal) == true)
                {
                    if (awaitingVerificationGoals.Contains(item.GoalId))
                        candidates.Add(new OwnerQuestionCandidate(new OwnerQuestion(item.Id, item.GoalId,
                            OwnerQuestionKind.PassedGateHold, item.Body, CanAnswer: false), item.RaisedAt));
                    continue;
                }
                if (item.GoalId is not null &&
                    item.CorrelationKey?.StartsWith(StalledEvidenceWaitAttention.Prefix, StringComparison.Ordinal) == true)
                {
                    if (goals.GetValueOrDefault(item.GoalId) == GoalStatus.Verified)
                        candidates.Add(new OwnerQuestionCandidate(new OwnerQuestion(item.Id, item.GoalId,
                            OwnerQuestionKind.PassedGateHold, item.Body, CanAnswer: false), item.RaisedAt));
                    continue;
                }
                if (OwnerExperimentReadingQuestion.From(item) is { } reading)
                {
                    candidates.Add(new OwnerQuestionCandidate(reading, item.RaisedAt));
                    continue;
                }
                if (item.GoalId is null ||
                    !SpecClarificationKey.IsSpecClarification(item.CorrelationKey)) continue;
                candidates.Add(new OwnerQuestionCandidate(new OwnerQuestion(item.Id, item.GoalId!,
                    OwnerQuestionKind.Clarification,
                    Field(item.Body, "Question:") ?? item.Subject,
                    Field(item.Body, "Blast radius:"),
                    Field(item.Body, "Refiner confidence:"),
                    Field(item.Body, "Proposed default:") ?? Field(item.Body, "Default:")), item.RaisedAt));
            }
        }

        return BuildSnapshot(goals, candidates);
    }

    private static OwnerQuestionSnapshot BuildSnapshot(IReadOnlyDictionary<string, GoalStatus> goals,
        IReadOnlyList<OwnerQuestionCandidate> candidates)
    {
        var eligible = new List<OwnerQuestionCandidate>();
        var hidden = new List<HiddenOwnerQuestion>();
        foreach (var candidate in candidates)
        {
            var reason = candidate.Question.Kind == OwnerQuestionKind.ExperimentReading ? null :
                !goals.TryGetValue(candidate.Question.GoalId, out var status) ? "goal-missing" :
                status switch
                {
                    GoalStatus.Completed => "goal-completed",
                    GoalStatus.Cancelled => "goal-cancelled",
                    GoalStatus.Superseded => "goal-superseded",
                    _ => candidate.BlocksActiveWork ? null : "non-blocking-kind"
                };
            if (reason is null) eligible.Add(candidate);
            else hidden.Add(new HiddenOwnerQuestion(candidate.Question, reason));
        }

        var seen = new HashSet<(string GoalId, string Text)>();
        var live = new HashSet<OwnerQuestion>();
        foreach (var candidate in eligible.OrderBy(candidate => candidate.CreatedAt))
        {
            var question = candidate.Question;
            if (seen.Add((question.GoalId.ToUpperInvariant(), question.Text))) live.Add(question);
            else hidden.Add(new HiddenOwnerQuestion(question, "duplicate"));
        }
        var raisedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var candidate in eligible.Where(candidate => live.Contains(candidate.Question)))
            raisedAt.TryAdd(candidate.Question.ItemId, candidate.CreatedAt);
        return new OwnerQuestionSnapshot(
            eligible.Select(candidate => candidate.Question).Where(live.Contains).ToArray(), hidden, raisedAt);
    }

    internal static string? Field(string body, string label)
    {
        using var reader = new StringReader(body);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[label.Length..].Trim();
                return value.Length == 0 ? null : value;
            }
        }
        return null;
    }

}
