using System.Security.Cryptography;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>Verifies green evidence from the existing mcg-test-result/1 contract.</summary>
public static class ContentBoundGreenNormalizedEvidenceCheck
{
    public static ContentBoundGreenTrxEvidenceFailure? FirstFailure(
        IReadOnlyList<string>? testResultPaths,
        IReadOnlyList<AcceptanceCohortEvidenceArtifact>? artifacts,
        int expectedExecutedTestCount,
        IReadOnlyCollection<string> requiredSelections,
        Func<string, IReadOnlyList<string>, IReadOnlyCollection<string>>? resolveSelectedTestIds = null)
    {
        if (!AcceptanceCohortGateEvidence.HasNormalizedTestResultPaths(testResultPaths))
            return new("normalized-paths-invalid", []);
        if (artifacts is not { Count: > 0 }) return new("normalized-artifacts-missing", []);
        if (expectedExecutedTestCount <= 0) return new("executed-count-missing", []);
        if (requiredSelections.Count == 0) return new("no-required-classes", []);

        var resultArtifacts = artifacts.Where(artifact => artifact.Kind == AcceptanceCohortCheckEvidence.NormalizedFormat)
            .OrderBy(artifact => artifact.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!resultArtifacts.Select(artifact => artifact.Path)
                .SequenceEqual(testResultPaths!, StringComparer.OrdinalIgnoreCase))
            return new("normalized-path-set-mismatch", []);

        var rows = new List<ResultRow>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in resultArtifacts)
        {
            var failure = ReadContentBoundRows(artifact, rows, ids);
            if (failure is not null) return new(failure, []);
        }

        if (rows.Any(row => row.Status is "failed" or "error")) return new("normalized-test-failed", []);
        if (rows.Count(row => row.Status != "skipped") != expectedExecutedTestCount)
            return new("executed-count-mismatch", []);

        var missing = new List<string>();
        var selectedSkipped = false;
        var allIds = rows.Select(row => row.Id).ToArray();
        foreach (var selection in requiredSelections)
        {
            var selectedIds = resolveSelectedTestIds?.Invoke(selection, allIds).ToHashSet(StringComparer.Ordinal);
            var selected = rows.Where(row => selectedIds is null
                ? string.Equals(row.Name, selection, StringComparison.Ordinal)
                : selectedIds.Contains(row.Id)).ToArray();
            if (selected.Length == 0) missing.Add(selection);
            selectedSkipped |= selected.Any(row => row.Status == "skipped");
        }
        if (missing.Count > 0)
            return new("required-class-missing", missing.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        return selectedSkipped ? new("normalized-test-skipped", []) : null;
    }

    private static string? ReadContentBoundRows(AcceptanceCohortEvidenceArtifact artifact,
        List<ResultRow> rows, HashSet<string> ids)
    {
        if (string.IsNullOrWhiteSpace(artifact.Path) || !Path.IsPathFullyQualified(artifact.Path) ||
            artifact.Length <= 0 || artifact.Sha256 is not { Length: 64 } ||
            artifact.Sha256.Any(character => !Uri.IsHexDigit(character)))
            return "normalized-content-hash-mismatch";

        try
        {
            // Hash and parse the same open file, denying writes throughout the read.
            using var stream = File.Open(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != artifact.Length ||
                !Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(artifact.Sha256, StringComparison.Ordinal))
                return "normalized-content-hash-mismatch";
            stream.Position = 0;
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (!UniqueObject(root) || Text(root, "reportFormat") != "CTRF" ||
                Text(root, "schemaVersion") != AcceptanceCohortCheckEvidence.NormalizedFormat ||
                !root.TryGetProperty("results", out var results) || !UniqueObject(results) ||
                !results.TryGetProperty("tool", out var tool) || !UniqueObject(tool) || Text(tool, "name") is null ||
                !results.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Array)
                return "normalized-content-invalid";

            foreach (var test in tests.EnumerateArray())
            {
                if (!UniqueObject(test) || Text(test, "testId") is not { } id ||
                    Text(test, "name") is not { } name || Text(test, "status") is not { } status ||
                    status is not ("passed" or "failed" or "error" or "skipped") || !ids.Add(id))
                    return "normalized-content-invalid";
                rows.Add(new(id, name, status));
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "normalized-content-hash-mismatch";
        }
        catch (JsonException)
        {
            return "normalized-content-invalid";
        }
    }

    private static bool UniqueObject(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() ==
        value.EnumerateObject().Count();

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
        property.GetString() is { Length: > 0 } text ? text : null;

    private sealed record ResultRow(string Id, string Name, string Status);
}
