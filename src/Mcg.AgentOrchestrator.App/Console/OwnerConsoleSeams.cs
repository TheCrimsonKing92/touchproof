using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerConsoleInput
{
    bool IsEditingLine { get; }
    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken);
}

internal interface IOwnerConsoleOutput
{
    void Write(string text);
    void WriteLine(string text);
}

internal interface IConductEventSource : IAsyncDisposable
{
    ValueTask<OwnerConductEvent> ReadAsync(CancellationToken cancellationToken);
    DateTimeOffset? LastActivity { get; }
}

internal interface IOwnerQuestionSource
{
    void Invalidate() { }
    Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken);

    async Task<OwnerQuestionSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        new(await ListOpenAsync(cancellationToken), []);
}

internal interface IOwnerAnswerSubmitter
{
    OwnerAnswerSubmission Submit(OwnerQuestion question, string answer);
    OwnerAnswerSubmission SubmitExperimentDecision(string experimentReference, OwnerExperimentDecisionForm form) =>
        throw new NotSupportedException("Experiment decisions are unavailable.");
    Task<OwnerAnswerIntentStatus?> ReadStatusAsync(string intentId, CancellationToken cancellationToken);
}

internal sealed record OwnerAnswerSubmission(string? IntentId);
internal sealed record OwnerAnswerIntentStatus(
    Mcg.AgentOrchestrator.Infrastructure.OperatorIntentStatus Status, string? Outcome = null);

internal interface IConductorLiveness
{
    ConductorLiveness IsRunning();
}

internal interface IOwnerDigestSummary
{
    IReadOnlyList<string> ReadSummaryLines();
    (IReadOnlyList<string> Lines, OwnerConsoleStructuredText? Fields) ReadStructuredSummary() => (ReadSummaryLines(), null);
}

internal interface IOwnerConsoleConductor
{
    int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error);
    int RunStructured(IReadOnlyList<string> args, TextWriter output, TextWriter error, Action<OwnerConsoleStructuredText> fields) =>
        Run(args, output, error);
}

internal interface IOwnerConsoleDigestReport
{
    int Run(TextWriter output);
    int RunStructured(TextWriter output, Action<OwnerConsoleStructuredText> fields) => Run(output);
}

internal interface IGoalEventTail
{
    IReadOnlyList<string> ReadLast(string goalId, int count);
}

internal sealed record OwnerConductEvent(DateTimeOffset Timestamp, string EventKind, string? GoalId, string Detail,
    OwnerActivityDetailEvidence? Evidence = null, Mcg.AgentOrchestrator.App.Orchestration.GoalProgressEventFields? GoalProgress = null);

internal enum OwnerQuestionKind { Clarification, HumanInput, StewardHold, ExperimentReading, Onboarding, PassedGateHold }

internal sealed record OwnerQuestion(
    string ItemId, string GoalId, OwnerQuestionKind Kind, string Text,
    string? BlastRadius = null, string? Confidence = null, string? ProposedDefault = null, bool CanAnswer = true);

internal sealed record OwnerQuestionSnapshot(
    IReadOnlyList<OwnerQuestion> Live, IReadOnlyList<HiddenOwnerQuestion> Hidden,
    IReadOnlyDictionary<string, DateTimeOffset>? RaisedAt = null);

internal sealed record HiddenOwnerQuestion(OwnerQuestion Question, string Reason);

internal sealed record OwnerGoalCard(
    string Id, string Title, GoalStatus State, AgentRole? CurrentRole, DateTimeOffset? LastEvent);
