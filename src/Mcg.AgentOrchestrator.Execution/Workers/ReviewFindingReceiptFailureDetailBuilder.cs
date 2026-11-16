using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ReviewFindingReceiptFailureDetailBuilder
{
    // Match ActionableCandidateRedFailureDetailRenderer's per-identity content caps.
    private const int MessageCharacterLimit = 2000;
    private const int StackLineLimit = 10;

    private sealed record ArmFailureDetail(int ArmIndex, FindingEvidenceArm Arm, IReadOnlyList<TestFailureDetail> Failures);
    private sealed record TestFailureDetail(
        string TestIdentity,
        string? SourcePath,
        string? Message,
        bool MessageTruncated,
        IReadOnlyList<string> StackLines,
        int OmittedStackLineCount,
        string? DetailUnavailable);
    private sealed record PathReadResult(IReadOnlyDictionary<string, TestResult> Failures, string? UnavailableCause);

    public static byte[] Build(FindingEvidenceReceipt receipt, Func<string, TestResultReadResult>? read = null)
    {
        var eligibleArms = (receipt.Arms ?? [])
            .Select((arm, index) => (Arm: arm, Index: index))
            .Where(item => item.Arm.Disposition == FindingEvidenceArmDisposition.Red &&
                item.Arm.FailingTestIdentities is { Count: > 0 })
            .ToArray();
        if (eligibleArms.Length == 0)
        {
            return JsonSerializer.SerializeToUtf8Bytes(receipt);
        }

        // Execution's reader shares the TRX identity resolver and ErrorInfo extraction
        // with AcceptanceTrxFailureReader, without an Infrastructure dependency.
        read ??= new TrxTestResultSource().Read;
        var pathResults = new Dictionary<string, PathReadResult>(StringComparer.Ordinal);
        var details = eligibleArms.Select(item => new ArmFailureDetail(
            item.Index,
            item.Arm.Arm,
            item.Arm.FailingTestIdentities!.Distinct(StringComparer.Ordinal)
                .Select(identity => BuildFailure(identity, item.Arm.TestResultPaths ?? [], read, pathResults))
                .ToArray())).ToArray();
        var body = JsonSerializer.SerializeToNode(receipt)!.AsObject();
        body.Add("FailureDetail", JsonSerializer.SerializeToNode(details));
        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    private static TestFailureDetail BuildFailure(
        string identity,
        IReadOnlyList<string> paths,
        Func<string, TestResultReadResult> read,
        Dictionary<string, PathReadResult> pathResults)
    {
        var causes = new List<string>();
        foreach (var path in paths)
        {
            if (!pathResults.TryGetValue(path, out var result))
            {
                result = ReadPath(path, read);
                pathResults.Add(path, result);
            }
            if (result.Failures.TryGetValue(identity, out var failure))
            {
                var message = string.IsNullOrEmpty(failure.Message) ? "not recorded" : failure.Message;
                var stackLines = string.IsNullOrEmpty(failure.Trace)
                    ? ["not recorded"]
                    : failure.Trace.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
                return new TestFailureDetail(
                    identity,
                    path,
                    message.Length > MessageCharacterLimit ? message[..MessageCharacterLimit] : message,
                    message.Length > MessageCharacterLimit,
                    stackLines.Take(StackLineLimit).ToArray(),
                    Math.Max(0, stackLines.Length - StackLineLimit),
                    null);
            }
            causes.Add(result.UnavailableCause ?? $"no matching entry in {path}");
        }
        var cause = causes.Count == 0 ? "no result paths recorded" : string.Join("; ", causes);
        return new TestFailureDetail(identity, null, null, false, [], 0,
            $"detail unavailable: {identity}: {cause}");
    }

    private static PathReadResult ReadPath(string path, Func<string, TestResultReadResult> read)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return new PathReadResult(new Dictionary<string, TestResult>(), $"path not fully qualified at {path}");
            }
            var result = read(path);
            if (result.Status == TestResultReadStatus.Readable)
            {
                var failures = result.Records.Where(record => record.Status != TestResultStatus.Passed)
                    .GroupBy(record => record.Id, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                return new PathReadResult(failures, null);
            }
            var cause = result.Status switch
            {
                TestResultReadStatus.Missing => $"missing at {path}",
                TestResultReadStatus.Unreadable => $"unreadable at {path}: {result.Detail}",
                TestResultReadStatus.Unparseable => $"unparseable at {path}: {result.Detail}",
                _ => $"reader error at {path}: {result.Status}"
            };
            return new PathReadResult(new Dictionary<string, TestResult>(), cause);
        }
        catch (Exception ex)
        {
            // Diagnostic access must not prevent packaging the authoritative receipt.
            // Receipt identity conflicts are checked by the caller, outside this boundary.
            return new PathReadResult(new Dictionary<string, TestResult>(),
                $"reader error at {path}: {ex.GetType().FullName}");
        }
    }
}
