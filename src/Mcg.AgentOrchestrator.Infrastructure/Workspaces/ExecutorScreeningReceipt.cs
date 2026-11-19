using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum ExecutorScreeningVerdict { Pass, Fail, Incomplete }

internal sealed record ExecutorScreeningLaneOutcome(string LaneName, string RemoteLaneName, string FilterHash,
    RemoteLaneOutcomeCode Outcome, IReadOnlyList<string> FailingTests,
    IReadOnlyList<string> IgnoredKnownDivergentTests, double DurationSeconds);

internal sealed record ExecutorScreeningExcludedLane(string LaneName, string Reason);

internal sealed record ExecutorScreeningReceipt(string ReceiptId, string GoalId, string CandidateCommit,
    string TreeSha, string MainSha, string ExecutorId, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    ExecutorScreeningVerdict Verdict, IReadOnlyList<ExecutorScreeningLaneOutcome> LaneOutcomes,
    IReadOnlyList<ExecutorScreeningExcludedLane> ExcludedLanes, int Version = 1);

internal sealed record ExecutorWholeGateTrialLaneResult(string LaneName, double? WallSeconds,
    int? Passed, int? Failed, IReadOnlyList<string> FailingTests, string? ExecutionFailure = null)
{
    internal static ExecutorWholeGateTrialLaneResult ReadTrx(string name, IReadOnlyList<string> paths,
        double? wallSeconds = null, int? exitCode = null, bool allowsEmptySelection = false)
    {
        var completion = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence(paths);
        var decision = GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
            new GoalAcceptanceVerifier.CommandResult(exitCode ?? 0, ""), completion,
            allowEmptySelection: allowsEmptySelection);
        if (!decision.Passed && decision.FailedPredicate is not
            (AcceptanceShardCompletionPredicates.FailingTrx or AcceptanceShardCompletionPredicates.AssemblyCleanupFailure))
            return new(name, wallSeconds, null, null, [], decision.FailedPredicate ?? "trx-incomplete");

        var passed = 0;
        var failed = 0;
        DateTimeOffset? started = null, finished = null;
        try
        {
            foreach (var path in paths)
            {
                var document = XDocument.Load(path);
                var counters = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "Counters");
                if (!ReadCount(counters, "passed", out var documentPassed) ||
                    !ReadCount(counters, "failed", out var documentFailed))
                    return new(name, wallSeconds, null, null, [], "trx-counts-unavailable");
                passed = checked(passed + documentPassed);
                failed = checked(failed + documentFailed);
                var times = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "Times");
                if (DateTimeOffset.TryParse((string?)times?.Attribute("start"), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var start) &&
                    DateTimeOffset.TryParse((string?)times?.Attribute("finish"), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var finish) && finish >= start)
                {
                    if (started is null || start < started.Value) started = start;
                    if (finished is null || finish > finished.Value) finished = finish;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or OverflowException)
        {
            return new(name, wallSeconds, null, null, [], "trx-counts-unavailable");
        }
        wallSeconds ??= (finished - started)?.TotalSeconds;
        var failingTests = GoalAcceptanceVerifierTestTelemetry.ExtractTrxFailureIdentities(paths);
        return new(name, wallSeconds, passed, failed, failingTests,
            !decision.Passed && failed == 0 ? decision.FailedPredicate : null);
    }

    private static bool ReadCount(XElement? counters, string name, out int count) =>
        int.TryParse((string?)counters?.Attribute(name), NumberStyles.None, CultureInfo.InvariantCulture, out count);
}

internal sealed record ExecutorWholeGateTrialMemory(long? PeakCommittedBytes, long? CommitLimitBytesAtPeak,
    int? SampleCount, int IntervalSeconds = 1, string? UnavailableReason = null)
{
    internal static ExecutorWholeGateTrialMemory Unavailable(string reason) => new(null, null, null, UnavailableReason: reason);
}

internal sealed record ExecutorWholeGateTrialLocalAttempt(string AttemptId, string CandidateCommit,
    double? WallSeconds, IReadOnlyList<ExecutorWholeGateTrialLaneResult> Lanes);

internal enum ExecutorWholeGateTrialStatus { Complete, Incomplete, Cancelled }

internal sealed record ExecutorWholeGateTrialReceipt(string ReceiptId, string GoalId, string CandidateCommit,
    string ExecutorId, DateTimeOffset StartedAt, double WallSeconds, ExecutorWholeGateTrialStatus Status,
    IReadOnlyList<ExecutorWholeGateTrialLaneResult> Lanes, ExecutorWholeGateTrialMemory Memory,
    ExecutorWholeGateTrialLocalAttempt? LocalAttempt, IReadOnlyList<ExecutorScreeningExcludedLane> UnsupportedChecks,
    int Version = 2, string Kind = "whole-gate-trial");

internal static class ExecutorWholeGateTrialComparison
{
    internal static string Format(ExecutorWholeGateTrialReceipt receipt)
    {
        if (receipt.LocalAttempt is not { WallSeconds: > 0 } local || local.CandidateCommit != receipt.CandidateCommit)
            return "TRIAL compare local attempt missing parity=unavailable";

        var remoteLanes = receipt.Lanes.ToLookup(lane => lane.LaneName, StringComparer.Ordinal);
        var localLanes = local.Lanes.ToLookup(lane => lane.LaneName, StringComparer.Ordinal);
        var differences = remoteLanes.Select(group => group.Key).Union(localLanes.Select(group => group.Key),
                StringComparer.Ordinal)
            .Where(name => !Matches(remoteLanes[name].ToArray(), localLanes[name].ToArray()))
            .Order(StringComparer.Ordinal).ToArray();
        var parity = differences.Length == 0 ? "identical" : $"different lanes=[{string.Join(", ", differences)}]";
        return FormattableString.Invariant($"TRIAL compare executor/local={receipt.WallSeconds / local.WallSeconds:F2} parity={parity}");
    }

    private static bool Matches(ExecutorWholeGateTrialLaneResult[] remote, ExecutorWholeGateTrialLaneResult[] local) =>
        remote is [{ ExecutionFailure: null, Passed: not null, Failed: not null } observed] &&
        local is [{ ExecutionFailure: null, Passed: not null, Failed: not null } expected] &&
        observed.Passed == expected.Passed && observed.Failed == expected.Failed;
}

// Receipts describe observations only. No acceptance or landing policy consumes this ledger.
internal static class ExecutorScreeningReceiptLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters =
        {
            new JsonStringEnumConverter<RemoteLaneOutcomeCode>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<ExecutorScreeningVerdict>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<ExecutorWholeGateTrialStatus>(JsonNamingPolicy.KebabCaseLower)
        }
    };

    internal static string ResolveStorePath(string worktreePath) => Path.Combine(
        AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath), ".orchestrator", "executor-screening-receipts.jsonl");

    internal static void Append(string path, ExecutorScreeningReceipt receipt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        SharedJsonlFile.AppendLine(path, JsonSerializer.Serialize(receipt, JsonOptions));
    }

    internal static IReadOnlyList<ExecutorScreeningReceipt> ReadAll(string path)
    {
        var receipts = new List<ExecutorScreeningReceipt>();
        foreach (var line in SharedJsonlFile.ReadLines(path))
        {
            try
            {
                if (JsonSerializer.Deserialize<ExecutorScreeningReceipt>(line, JsonOptions) is { } receipt &&
                    receipt.Version == 1 && !string.IsNullOrWhiteSpace(receipt.GoalId) &&
                    !string.IsNullOrWhiteSpace(receipt.CandidateCommit) && receipt.LaneOutcomes is not null &&
                    receipt.ExcludedLanes is not null) receipts.Add(receipt);
            }
            catch (JsonException) { }
        }
        return receipts;
    }

    internal static void Append(string path, ExecutorWholeGateTrialReceipt receipt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        SharedJsonlFile.AppendLine(path, JsonSerializer.Serialize(receipt, JsonOptions));
    }

    internal static IReadOnlyList<ExecutorWholeGateTrialReceipt> ReadTrials(string path)
    {
        var receipts = new List<ExecutorWholeGateTrialReceipt>();
        foreach (var line in SharedJsonlFile.ReadLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("version", out var version) &&
                    version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == 2 &&
                    root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String &&
                    kind.GetString() == "whole-gate-trial" &&
                    root.Deserialize<ExecutorWholeGateTrialReceipt>(JsonOptions) is { Lanes: not null, Memory: not null } receipt &&
                    !string.IsNullOrWhiteSpace(receipt.GoalId) && !string.IsNullOrWhiteSpace(receipt.CandidateCommit))
                    receipts.Add(receipt);
            }
            catch (JsonException) { }
        }
        return receipts;
    }

    internal static ExecutorScreeningVerdict DecideVerdict(IReadOnlyList<ExecutorScreeningLaneOutcome> outcomes)
    {
        if (outcomes.Any(lane => lane.Outcome == RemoteLaneOutcomeCode.RemoteRed && lane.FailingTests.Count > 0))
            return ExecutorScreeningVerdict.Fail;
        return outcomes.Count > 0 && outcomes.All(lane => lane.Outcome == RemoteLaneOutcomeCode.Accepted ||
            lane.Outcome == RemoteLaneOutcomeCode.RemoteRed && lane.FailingTests.Count == 0 &&
            lane.IgnoredKnownDivergentTests.Count > 0)
            ? ExecutorScreeningVerdict.Pass : ExecutorScreeningVerdict.Incomplete;
    }
}
