namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Activity owns the explanation. Shared dialogs own wrapping, selection and copying.
internal static class OwnerActivityDetailPresentation
{
    internal static (string Text, OwnerConsoleFieldSnapshot Fields, OwnerConsoleStructuredText Structured) Build(
        OwnerConsoleActivityItem item, string why, string next, string act,
        OwnerConsoleFieldSnapshot? additional = null)
    {
        var events = item.Evidence ?? [new(item.Timestamp, item.Kind, item.GoalId, item.Detail)];
        var parsed = events.Select(source => OwnerActivityDiagnosticParser.Parse(source.Evidence?.Original ?? source.Detail)).ToArray();
        var rejection = events.Count == 1 && parsed[0].Complete ? parsed[0] : null;
        var summary = Summary(item);
        if (rejection is not null)
        {
            var roles = rejection.Fields.Where(field => field.Label == "role").ToArray();
            var role = events[0].Evidence?.Fields.FirstOrDefault(field => field.Key == "recorded/role")?.Value ??
                (events[0].EventKind == "goal-lifecycle" && OwnerActivityNarrator.Role(events[0]) != "Worker"
                    ? OwnerActivityNarrator.Role(events[0]) : roles.Length == 1 ? roles[0].Value : null);
            summary = (role is "Developer" or "Tester" or "Reviewer" or "Planner" or "Researcher" or "Author" ? role : "Worker") +
                " completion was rejected.";
            var reasons = rejection.Fields.Where(field => field.Label == "reason").ToArray();
            why = reasons.Length == 1 ? reasons[0].Value! : "Multiple rejection reasons were recorded; inspect Diagnostics.";
            // A rejection does not itself establish a retry or incorrect code.
            if (item.OwnerQuestionId is null && item.Resolution is null) next = "The next step was not recorded for this event.";
        }
        else if (why.Contains('=') || why == item.Detail || why.Length == 0)
            why = "The reason was not recorded separately; inspect the event evidence below.";
        if (item.Phrase.Length == 0 && item.OwnerQuestionId is null && item.Resolution is null)
        {
            next = "The next step was not recorded for this event.";
            act = "No owner action was recorded for this event.";
        }

        var fields = item.Fields() with { Fields = [.. item.Fields().Fields.Select(field => field.Key switch
        {
            "phrase" => field with { Value = summary }, "why" => field with { Value = why },
            "next" => field with { Value = next }, "act" => field with { Value = act }, _ => field
        })] };
        var values = fields.Fields.ToList();
        var lines = new List<string>();
        var keys = new Dictionary<int, string>();
        Add("What happened", summary, "phrase");
        if (item.GoalPrefixes is { Count: >= 2 } prefixes && item.Spans?.Titles is { } titles && titles.Count == prefixes.Count)
            for (var i = 0; i < prefixes.Count; i++)
                lines.Add("  " + prefixes[i] + " " + item.Phrase.Substring(titles[i].Start, titles[i].Length));
        lines.Add(""); Add("Reason", why, "why");
        lines.Add(""); Add("Next step", next, "next");
        lines.Add(""); Add("Owner action", act, "act");
        lines.Add(""); lines.Add("Diagnostics");
        Add("Kind", item.Kind, "kind");
        if (item.OwnerQuestionId is not null) Add("Question ID", item.OwnerQuestionId, "question-id");
        for (var i = 0; i < events.Count; i++)
        {
            var source = events[i];
            var prefix = $"event/{i}/";
            if (events.Count > 1) { lines.Add(""); lines.Add($"Event {i + 1}"); }
            Diagnostic(new(prefix + "goal", "Goal ID", source.GoalId));
            Diagnostic(new(prefix + "timestamp", "Timestamp", source.Timestamp.ToString("O")));
            foreach (var field in source.Evidence?.Fields ?? []) Diagnostic(field with { Key = prefix + field.Key });
            foreach (var field in parsed[i].Fields) Diagnostic(field with { Key = prefix + field.Key });
            var original = source.Evidence?.Original ?? source.Detail;
            var originalKey = prefix + "original";
            values.Add(new(originalKey, events.Count > 1 ? $"Original event {i + 1}" : "Original event", original));
            if (!parsed[i].Complete && original.Length > 0)
            {
                lines.Add(""); lines.Add(events.Count > 1 ? $"Original event {i + 1}" : "Original event");
                Add("Source", original, originalKey);
            }
        }
        if (additional is not null)
            foreach (var field in additional.Fields)
            {
                var key = "resolution/" + field.Key;
                values.Add(field with { Key = key });
                if (field.Available && (field.Key != "evidence" || field.Value!.Length > 0)) Add(field.Label, field.Value!, key);
            }
        fields = fields with { Fields = [.. values] };
        var structured = new OwnerConsoleStructuredText(fields, FieldKeys: keys, ContinuationIndent: 4);
        return (string.Join("\n", lines), fields, structured);

        void Diagnostic(OwnerConsoleField field)
        {
            values.Add(field);
            if (field.Available) Add(field.Label, field.Value!, field.Key);
        }
        void Add(string label, string value, string key)
        {
            var paragraphs = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var line = 0; line < paragraphs.Length; line++)
            {
                keys[lines.Count] = key;
                lines.Add((line == 0 ? label + ": " : "    ") + paragraphs[line]);
            }
        }
    }

    private static string Summary(OwnerConsoleActivityItem item)
    {
        if (item.GoalPrefixes is { Count: >= 2 } prefixes && item.Spans?.Titles is { } titles && titles.Count == prefixes.Count)
        {
            var end = titles[^1].Start + titles[^1].Length + titles[^1].TrailingLength;
            return $"{item.Timestamp.ToLocalTime():HH:mm:ss} " + item.Phrase[..titles[0].Start] +
                $"{prefixes.Count} goals" + item.Phrase[end..];
        }
        if (item.Phrase.Length == 0 || item.Phrase.Contains('=') || item.Phrase.Contains('\n'))
            return "An Activity event was recorded.";
        var summary = OwnerActivityNarrator.Line(item);
        return item.GoalTitle.Length > 0 && !summary.Contains(item.GoalTitle, StringComparison.Ordinal)
            ? summary + " (" + item.GoalTitle + ")" : summary;
    }

    internal static (string Text, OwnerConsoleStructuredText Structured) Resolved(OwnerConsoleActivityItem item,
        OwnerQuestionResolution resolution, TimeZoneInfo zone)
    {
        var detail = Build(item, "The recorded owner question was resolved.", resolution.NextStage,
            $"Resolved by {resolution.Answerer} at {TimeZoneInfo.ConvertTime(resolution.ResolvedAt, zone):HH:mm:ss}.",
            OwnerQuestionResolutionText.Fields(resolution));
        return (detail.Text, detail.Structured);
    }
}
