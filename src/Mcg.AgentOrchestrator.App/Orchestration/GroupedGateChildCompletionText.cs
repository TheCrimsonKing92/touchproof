using System.Text;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GroupedGateChildCompletionText
{
    private const int RenderedDetailLimit = 512;

    internal static string RecognizeVerdict(string? verdict)
    {
        if (string.Equals(verdict, "passed", StringComparison.OrdinalIgnoreCase)) return "passed";
        if (string.Equals(verdict, "failed", StringComparison.OrdinalIgnoreCase)) return "failed";
        return verdict is CohortStableSlotAcquisitionRounds.DeferredVerdict or
            MergeTrainChildOutcome.InfrastructureFailure or MergeTrainChildOutcome.Invalidated or
            MergeTrainChildOutcome.NoReceipt || CohortChildOutcome.IsPublishedVerdict(verdict) ? verdict! : "unknown";
    }

    internal static string RenderDetail(string detail)
    {
        var rendered = new StringBuilder("\"");
        var lastRuneStart = rendered.Length;
        foreach (var rune in detail.EnumerateRunes())
        {
            var escaped = Escape(rune);
            if (rendered.Length + escaped.Length > RenderedDetailLimit - 1)
            {
                // Reserve the closing quote and truncation marker without cutting an escape or rune.
                if (rendered.Length > RenderedDetailLimit - 2) rendered.Length = lastRuneStart;
                return rendered.Append('…').Append('"').ToString();
            }
            lastRuneStart = rendered.Length;
            rendered.Append(escaped);
        }
        return rendered.Append('"').ToString();
    }

    private static string Escape(Rune rune) => rune.Value switch
    {
        '"' => "\\\"",
        '\\' => "\\\\",
        '\b' => "\\b",
        '\f' => "\\f",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        // Some event readers ignore quoting; escape '=' to prevent injected key=value fields.
        '=' or 0x2028 or 0x2029 or < 0x20 or >= 0x7f and <= 0x9f => $"\\u{rune.Value:x4}",
        _ => rune.ToString()
    };
}
