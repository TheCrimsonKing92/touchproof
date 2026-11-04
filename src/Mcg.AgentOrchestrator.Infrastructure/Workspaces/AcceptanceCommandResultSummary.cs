using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptanceProcessRunner;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns command-check output summaries, timeout diagnostics and appended diagnostic receipts.
internal static class AcceptanceCommandResultSummary
{
    internal static string? ExtractResultSummary(string output)
    {
        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line =>
                line.Contains("Failed:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Passed:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Total:", StringComparison.OrdinalIgnoreCase));
    }

    internal static string? TailOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        const int maxChars = 2000;
        const int maxLines = 40;

        var trimmed = output.Trim();
        var lines = trimmed.Split('\n');
        var tail = lines.Length <= maxLines
            ? trimmed
            : string.Join('\n', lines[^maxLines..]);

        return tail.Length <= maxChars ? tail : tail[^maxChars..];
    }

    internal static string BuildTimeoutSummary(CommandResult result)
    {
        var fallback = new AcceptanceGateEngineSettings().ResolveCheckTimeout(null);
        return $"elapsed={FormatTimeout(result.Elapsed ?? result.Timeout ?? fallback)} budget={FormatTimeout(result.Timeout ?? fallback)}"
            + AcceptanceLaneStallPolicy.FormatStallSuffix(result.LaneStallSilence, result.LaneStallLimit);
    }

    internal static string BuildTimeoutFailureName(AcceptanceManifestCheck check, CommandResult result) =>
        $"acceptance-check-timeout: {AcceptanceArtifactFileNames.Slug(check.Name)} {BuildTimeoutSummary(result)}";

    internal static string BuildTimeoutOutput(CommandResult result)
    {
        var details = new List<string>
        {
            $"Verification command timed out after {BuildTimeoutSummary(result)}.",
        };

        if (!string.IsNullOrWhiteSpace(result.CommandLine))
            details.Add($"Command: {result.CommandLine}");
        if (!string.IsNullOrWhiteSpace(result.StdoutPath))
            details.Add($"stdout: {result.StdoutPath}");
        if (!string.IsNullOrWhiteSpace(result.StderrPath))
            details.Add($"stderr: {result.StderrPath}");

        var tail = TailOutput(result.Output);
        if (!string.IsNullOrWhiteSpace(tail))
        {
            details.Add("Last output:");
            details.Add(tail);
        }

        return string.Join(Environment.NewLine, details);
    }

    private static string FormatTimeout(TimeSpan timeout) =>
        timeout.TotalSeconds >= 60
            ? $"{timeout.TotalMinutes:0.#}m"
            : $"{timeout.TotalSeconds:0.#}s";

    internal static string? ManagedDotnet(CommandResult result, string? summary, bool transientCompilerLockRetried)
    {
        summary = AppendResourceReceipt(summary, result.ResourceAccounting, result.ResourceAccountingExpected);
        if (!transientCompilerLockRetried)
        {
            return summary;
        }

        const string remediation = "build artifact lock detected; holder attributed; remediation retried";
        return string.IsNullOrWhiteSpace(summary)
            ? remediation
            : $"{remediation}; {summary}";
    }

    internal static string? GenericCommand(CommandResult result, string? summary)
    {
        if (result.ResolvedCommandPath is { } resolvedPath)
        {
            var receipt = $"resolved_command={ProcessInvokerAccess.QuoteForDisplay(resolvedPath)}";
            summary = string.IsNullOrWhiteSpace(summary) ? receipt : $"{summary}; {receipt}";
        }
        return AppendResourceReceipt(summary, result.ResourceAccounting, result.ResourceAccountingExpected);
    }

    private static string? AppendResourceReceipt(
        string? summary,
        TaskProcessResourceAccounting? accounting,
        bool accountingExpected)
    {
        var receipt = accounting is null
            ? accountingExpected ? "RESOURCE phase=gate cpu_ms=0 peak_mem_bytes=0 io_bytes=0 accounting_source=accounting-unavailable" : null
            : $"RESOURCE phase=gate cpu_ms={accounting.CpuMilliseconds} peak_mem_bytes={accounting.PeakMemoryBytes} io_bytes={accounting.IoBytes} accounting_source={accounting.AccountingSource}";
        if (receipt is null)
        {
            return summary;
        }

        return string.IsNullOrWhiteSpace(summary)
            ? receipt
            : $"{summary}; {receipt}";
    }
}
