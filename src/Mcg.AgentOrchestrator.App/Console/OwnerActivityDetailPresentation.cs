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
        var values = fields.Fields.Where(field => !string.IsNullOrWhiteSpace(field.Value) &&
            !(field.Key == "detail" && events.Count == 1 && field.Value == (events[0].Evidence?.Original ?? events[0].Detail))).ToList();
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
            var section = events.Count > 1 ? $"Original event {i + 1}" : "Original event";
            var parts = OwnerGoalProgressDetail.Fields(source);
            if (source.GoalProgress is null && parts.Count == 0) Diagnostic(new(prefix + "goal", "Goal ID", source.GoalId), section);
            Diagnostic(new(prefix + "timestamp", "Timestamp", source.Timestamp.ToString("O")), section);
            foreach (var field in source.Evidence?.Fields ?? []) Diagnostic(field with { Key = prefix + field.Key }, section);
            foreach (var field in parsed[i].Fields) Diagnostic(field with { Key = prefix + field.Key }, section);
            var original = source.Evidence?.Original ?? source.Detail;
            var originalKey = prefix + "original";
            if (source.GoalProgress is not null || parts.Count > 0)
            {
                lines.Add(""); lines.Add(section);
                foreach (var field in parts)
                {
                    Original(field with { Key = prefix + field.Key });
                }
                Original(new(originalKey, "Raw event", original));
            }
            else
            {
                Store(new(originalKey, section, original), section);
                if (!parsed[i].Complete && original.Length > 0)
                {
                    lines.Add(""); lines.Add(section);
                    Add("Source", original, originalKey);
                }
            }

            void Original(OwnerConsoleField field)
            {
                if (Store(field, section)) Add(field.Label, field.Value!, field.Key);
            }
        }
        if (additional is not null)
            foreach (var field in additional.Fields)
            {
                var key = "resolution/" + field.Key;
                if (Store(field with { Key = key }, "Resolution")) Add(field.Label, field.Value!, key);
            }
        fields = fields with { Fields = [.. values] };
        var structured = new OwnerConsoleStructuredText(fields, FieldKeys: keys, ContinuationIndent: 4,
            FooterHint: "F2 fields  Ctrl+C copy field");
        return (string.Join("\n", lines), fields, structured);

        void Diagnostic(OwnerConsoleField field, string section)
        {
            if (Store(field, section)) Add(field.Label, field.Value!, field.Key);
        }
        bool Store(OwnerConsoleField field, string section)
        {
            if (string.IsNullOrWhiteSpace(field.Value) || values.Any(existing => existing.Key == field.Key)) return false;
            if (values.Any(existing => existing.Label == field.Label))
            {
                var label = section + ": " + field.Label;
                var occurrence = 2;
                field = field with { Label = label };
                while (values.Any(existing => existing.Label == field.Label)) field = field with { Label = label + " " + occurrence++ };
            }
            values.Add(field);
            return true;
        }
        void Add(string label, string value, string key)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
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
