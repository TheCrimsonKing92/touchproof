namespace Mcg.AgentOrchestrator.Infrastructure;

// A case is a recorded round, not a successful execution or a gate verdict.
public sealed record ExperimentReplaySource(string Identity, string Sha256);
public sealed record ExperimentReplayGate(string AttemptId, DateTimeOffset StartedAt, string Outcome,
    IReadOnlyList<string> FailingTests, IReadOnlyList<ExperimentReplaySource> Sources);
public sealed record ExperimentReplayCase(string Id, string Project, string GoalId, string TaskId,
    DateTimeOffset DispatchedAt, DateTimeOffset CompletedAt, string? BaseCommit, string? CandidateCommit,
    string? ObservedMain, string Replayability, IReadOnlyList<string> Reasons,
    IReadOnlyList<ExperimentReplayGate> Gates, IReadOnlyList<ExperimentReplaySource> Sources);

// Registration owns intent and fixed inputs; it never asserts that execution succeeded.
public sealed record ExperimentReplayRegistration(string Id, string Project, DateTimeOffset RegisteredAt,
    int Seed, string RuntimeIdentity, string ToolchainIdentity, string ConfigurationIdentity,
    IReadOnlyList<ExperimentReplayCase> Cases, ExperimentReplaySource ValueBoundEvidence,
    int RequiredCases = 20, int Repetitions = 3, int Warmups = 2, int CalibrationPairs = 10,
    int BootstrapResamples = 10000, string ResetPolicy = "fresh-work-directory; immutable-dependency-cache; warm",
    int ProcedureTimeoutSeconds = 600, int RunTimeoutSeconds = 14400,
    string DecisionRule = "descriptive; 20 valid cases; AA bias <=5%; AA interval within +/-10% mean cost");
public sealed record ExperimentReplaySlot(int Ordinal, string CaseId, string Stage, int Pair,
    string Arm, bool Warmup);
public sealed record ExperimentReplaySelection(string Arm, IReadOnlyList<string> Classes,
    IReadOnlyList<string> Projects, string Outcome, IReadOnlyList<string> Diagnostics);
public sealed record ExperimentReplayResources(long CpuMilliseconds, long PeakMemoryBytes, double WallMilliseconds,
    IReadOnlyDictionary<string, int> LaunchesByExecutable, string AccountingSource,
    bool LaunchAttributionComplete, uint TotalProcesses,
    IReadOnlyDictionary<string, string>? ExecutableIdentities = null);
public sealed record ExperimentReplayExecution(string Id, string RunId, ExperimentReplaySlot Slot,
    string State, string? Reason, string HostIdentity, string RuntimeIdentity, string ToolchainIdentity,
    string Priority, string PowerThrottling, DateTimeOffset StartedAt,
    ExperimentReplayResources? Resources, ExperimentReplaySelection? Selection,
    IReadOnlyList<string> FailingTests, IReadOnlyList<ExperimentReplaySource> Sources);
public sealed record ExperimentReplayRun(string Id, string RegistrationId, string State, string? Reason,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);
public sealed record ExperimentReplayInterval(double Estimate, double Lower, double Upper, int Units, string Unit);
public sealed record ExperimentReplayCalibration(string State, string Reason, double? SignedBiasPercent,
    double? MeanCost, ExperimentReplayInterval? Difference, IReadOnlyList<double> PairDifferences);
public sealed record ExperimentReplayFault(string Id, string RunId, string CaseId, string Shape, int Seed,
    string BaseCommit, string State, string Reason, IReadOnlyList<ExperimentReplaySource> Patches,
    IReadOnlyList<string> ExpectedTests, IReadOnlyList<ExperimentReplayExecution> Executions,
    IReadOnlyList<string> ConfirmedDefects, IReadOnlyList<string> ACatches, IReadOnlyList<string> BCatches);
