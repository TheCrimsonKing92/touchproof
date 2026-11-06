using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Parses only the kernel's dispatch-rejection envelope. Values are source slices,
// including transport quotes/escapes; this is deliberately not a general key=value guesser.
internal static class OwnerActivityDiagnosticParser
{
    internal sealed record Result(bool Complete, IReadOnlyList<OwnerConsoleField> Fields);

    internal static Result Parse(string source)
    {
        const string marker = "DISPATCH_REJECTED ";
        const string policyPrefix = "Goal is in Failed state; TaskFailed: ";
        const string batchPrefix = "Batch loop tick ";
        const string policySuffix = "; operator action required";
        var payload = source;
        // These are literal producer envelopes, not evidence associations with other events.
        if (payload.StartsWith(batchPrefix, StringComparison.Ordinal))
        {
            var separator = payload.IndexOf(": escalated at Failed — ", StringComparison.Ordinal);
            if (separator < 0 || !payload[batchPrefix.Length..separator].All(char.IsDigit)) return new(false, []);
            payload = payload[(separator + ": escalated at Failed — ".Length)..];
        }
        if (payload.StartsWith(policyPrefix, StringComparison.Ordinal))
        {
            var pending = payload.IndexOf(policySuffix + ConductorOutcomeText.PendingBlockedIntentClause, StringComparison.Ordinal);
            if (pending >= 0) payload = payload[..(pending + policySuffix.Length)];
            if (payload.EndsWith(policySuffix, StringComparison.Ordinal))
                payload = payload[policyPrefix.Length..^policySuffix.Length];
        }
        if (!payload.StartsWith(marker, StringComparison.Ordinal)) return new(false, []);

        var fields = new List<OwnerConsoleField>();
        var index = marker.Length;
        while (index < payload.Length)
        {
            var start = index;
            while (index < payload.Length && (char.IsAsciiLetterOrDigit(payload[index]) || payload[index] is '_' or '-')) index++;
            if (index == start || index >= payload.Length || payload[index++] != '=') return new(false, []);
            var key = payload[start..(index - 1)];
            start = index;
            if (index < payload.Length && payload[index] == '"')
            {
                index++;
                var closed = false;
                while (index < payload.Length)
                {
                    var ch = payload[index++];
                    if (ch == '\\')
                    {
                        if (index == payload.Length || payload[index] is not ('\\' or '"')) return new(false, []);
                        index++;
                    }
                    else if (ch == '"') { closed = true; break; }
                }
                if (!closed) return new(false, []);
            }
            else
            {
                while (index < payload.Length && payload[index] != ' ')
                {
                    if (char.IsWhiteSpace(payload[index]) || payload[index] == '"') return new(false, []);
                    index++;
                }
            }
            fields.Add(new($"diagnostic/{key}/{fields.Count(field => field.Label == key)}", key, payload[start..index]));
            if (index == payload.Length) break;
            if (payload[index++] != ' ' || index == payload.Length) return new(false, []);
        }
        // Required producer fields distinguish this grammar from coincidental prose.
        string[] required = ["role", "task", "worktree", "dispatched_at", "baseline_head", "post_dispatch_commits",
            "changed_paths", "tests_status", "blockers", "verification_recognized", "reason", "detail"];
        return required.All(key => fields.Any(field => field.Label == key)) ? new(true, fields) : new(false, []);
    }
}
