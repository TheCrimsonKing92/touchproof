using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>A lossless delivery view. Persisted specification records remain the authority.</summary>
public static class CurrentSpecificationProjection
{
    // Keep the established source representation byte-compatible; delivery changes must not
    // change the identity of an authoritative source artifact.
    public static byte[] SourceBytes(RefinedSpec spec) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        spec.BehavioralContract,
        spec.AcceptanceCriteria,
        VerificationClass = spec.VerificationClass.ToString(),
        spec.Decisions,
        spec.OpenQuestions,
        spec.OperatorOwnedAcceptanceCriteria,
        ClarificationAnswerHistory = spec.AuthoritativeClarificationAnswerHistory.Select(answer => new
        {
            answer.Id,
            answer.Text,
            Origin = answer.Origin.ToString(),
            answer.SupersededByAnswerId,
            answer.BriefVersion
        }).ToArray()
    });

    public static byte[] Project(RefinedSpec spec)
    {
        var source = SourceBytes(spec);
        var root = JsonNode.Parse(source)!.AsObject();
        // Only body fields share content. Questions, statuses, scope, rationale and provenance
        // stay on their original records; same-topic but different answers are never merged.
        var slots = new List<(JsonObject Record, string Property, string Body)>();
        foreach (var (collection, property) in new[]
        {
            ("Decisions", "Choice"), ("OpenQuestions", "Answer"), ("ClarificationAnswerHistory", "Text")
        })
        {
            foreach (var record in root[collection]!.AsArray().OfType<JsonObject>())
                if (record[property] is JsonValue value && value.TryGetValue<string>(out var body))
                    slots.Add((record, property, body));
        }

        var bodies = new JsonObject();
        foreach (var group in slots.GroupBy(slot => slot.Body, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            var id = $"body-{bodies.Count + 1}";
            var reference = new JsonObject { ["$body"] = id };
            // This is a representation-size comparison, not a content cap.
            if (group.Count() * JsonSerializer.Serialize(group.Key).Length <=
                JsonSerializer.Serialize(group.Key).Length + group.Count() * reference.ToJsonString().Length + id.Length + 4)
                continue;
            bodies[id] = group.Key;
            foreach (var slot in group)
                slot.Record[slot.Property] = reference.DeepClone();
        }
        if (bodies.Count == 0) return source;
        root["SharedBodies"] = bodies;
        root["BodyReferenceContract"] = "$body resolves verbatim in this document's SharedBodies; metadata remains on each record.";
        var projected = JsonSerializer.SerializeToUtf8Bytes(root);
        return projected.Length < source.Length ? projected : source;
    }

    public static string ProjectObjective(string objective, RefinedSpec? spec)
    {
        if (spec is null) return objective;
        // Preserve original line endings and all unrecognized material. Recognize criteria
        // only inside an explicitly headed section, and only by ordinal exact equality.
        var lines = System.Text.RegularExpressions.Regex.Split(objective, "(?<=\\n)");
        var inCriteria = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r', '\n');
            if (line.StartsWith('#'))
            {
                inCriteria = line.TrimStart('#').Trim().Equals("Acceptance criteria", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inCriteria) continue;
            for (var criterion = 0; criterion < spec.AcceptanceCriteria.Count; criterion++)
            {
                if (line != $"{criterion + 1}. {spec.AcceptanceCriteria[criterion]}" &&
                    line != $"- {spec.AcceptanceCriteria[criterion]}") continue;
                var replacement = $"- See current specification, acceptance criterion {criterion + 1}.";
                if (replacement.Length < line.Length)
                    lines[i] = replacement + lines[i][line.Length..];
                break;
            }
        }
        return string.Concat(lines);
    }
}
