using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

public sealed record ReviewFindingParseDiagnostic(
    int? ItemIndex,
    string? StableId,
    FindingSeverity? Severity,
    string Reason);

// Accepted findings and rejected format diagnostics have separate authority. A rejected
// fragment must never enter the finding ledger or authorize an evidence mutation.
public sealed record ReviewFindingParseResult(
    ReviewFindingRound Round,
    IReadOnlyList<ReviewFindingParseDiagnostic> AdvisoryRejections,
    ReviewFindingParseDiagnostic? FatalDiagnostic);

public static partial class ReviewFindingConvergence
{
    public const string FindingFormatViolationCode = "ERR_REVIEW_FINDING_FORMAT";
    public const string NoFileAnchor = "(no file anchor)";

    public static ReviewFindingParseResult ParseJson(
        string findingsJson, string touchedAnchorsJson, string? workingDirectory = null)
    {
        var accepted = new List<ReviewFinding>();
        var warnings = new List<ReviewFindingParseDiagnostic>();
        ReviewFindingParseDiagnostic? fatal = null;
        var empty = new ReviewFindingRound([], []);
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new FindingSeverityJsonConverter());
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
            using var document = JsonDocument.Parse(findingsJson);
            var anchors = JsonSerializer.Deserialize<List<ReviewFindingLocation>>(touchedAnchorsJson, options);
            if (document.RootElement.ValueKind == JsonValueKind.Null || anchors is null)
                throw new ArgumentException("findings or touched_anchors JSON was null.");
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("findings JSON must be an array.");
            ValidateAnchors(anchors);

            var items = document.RootElement.EnumerateArray().Select((element, index) =>
                (Element: element, Index: index,
                 StableId: ReadStableId(element), Severity: ReadSeverity(element, options))).ToArray();
            var duplicates = items.Where(item => !string.IsNullOrWhiteSpace(item.StableId))
                .GroupBy(item => item.StableId!, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (var item in items)
            {
                string? rejection = null;
                if (item.StableId is not null && duplicates.TryGetValue(item.StableId, out var group))
                {
                    rejection = $"Duplicate stable_id '{item.StableId}' in reviewer review findings.";
                    // A mixed duplicate group is a whole-round error even if its first item is advisory.
                    if (group.Any(member => member.Severity != FindingSeverity.Advisory))
                    {
                        fatal ??= new(item.Index, item.StableId, FindingSeverity.Blocking, rejection);
                        continue;
                    }
                }
                else
                {
                    try
                    {
                        if (item.Element.ValueKind == JsonValueKind.Object && item.Element.EnumerateObject()
                            .Count(property => string.Equals(property.Name, "stable_id", StringComparison.OrdinalIgnoreCase)) > 1)
                            throw new ArgumentException("Every review finding requires one unambiguous stable_id.");
                        var finding = item.Element.Deserialize<ReviewFinding>(options);
                        ValidateFindings([finding!]);
                        accepted.Add(NormalizeFindingLocation(finding! with { Severity = item.Severity }, workingDirectory));
                    }
                    catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
                    {
                        rejection = ex.Message;
                    }
                }

                if (rejection is null)
                    continue;
                var diagnostic = new ReviewFindingParseDiagnostic(item.Index, item.StableId, item.Severity, rejection);
                if (item.Severity == FindingSeverity.Advisory)
                    warnings.Add(diagnostic);
                else
                    fatal ??= diagnostic;
            }

            return new(new(accepted, anchors), warnings, fatal);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return new(empty, warnings, new(null, null, null, ex.Message));
        }
    }

    private static ReviewFinding NormalizeFindingLocation(ReviewFinding finding, string? workingDirectory)
    {
        var file = finding.Location.File;
        if (file == NoFileAnchor)
            return finding;

        try
        {
            var path = file.Replace('\\', Path.DirectorySeparatorChar);
            var rooted = Path.IsPathRooted(path);
            if (!string.IsNullOrWhiteSpace(workingDirectory) && Path.IsPathFullyQualified(workingDirectory) &&
                (!rooted || Path.IsPathFullyQualified(path)))
            {
                var root = Path.GetFullPath(workingDirectory);
                var fullPath = Path.GetFullPath(path, root);
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var boundary = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
                if (fullPath.Length > root.Length && fullPath.StartsWith(boundary, comparison))
                    return rooted
                        ? finding with { Location = finding.Location with { File = Path.GetRelativePath(root, fullPath).Replace('\\', '/') } }
                        : finding;
            }
            else if (!rooted)
            {
                // API results may have no working directory. Ordinary relative anchors retain their identity.
                var depth = 0;
                var contained = true;
                foreach (var segment in file.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (segment == "..")
                    {
                        if (--depth < 0)
                        {
                            contained = false;
                            break;
                        }
                    }
                    else if (segment != ".")
                        depth++;
                }
                if (contained && depth > 0 && !file.Contains('\0'))
                    return finding;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            // An unusable location must not reject an otherwise valid finding.
        }

        return finding with
        {
            Location = finding.Location with { File = NoFileAnchor },
            Description = finding.Description.Contains(file, StringComparison.Ordinal)
                ? finding.Description
                : $"{finding.Description} (location outside repository: {file})"
        };
    }

    private static string? ReadStableId(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        var properties = element.EnumerateObject()
            .Where(property => string.Equals(property.Name, "stable_id", StringComparison.OrdinalIgnoreCase)).ToArray();
        return properties.Length == 1 && properties[0].Value.ValueKind == JsonValueKind.String
            ? properties[0].Value.GetString() : null;
    }

    private static FindingSeverity ReadSeverity(JsonElement element, JsonSerializerOptions options)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return FindingSeverity.Blocking;
        var properties = element.EnumerateObject()
            .Where(property => string.Equals(property.Name, "severity", StringComparison.OrdinalIgnoreCase)).ToArray();
        return properties.Length == 1
            ? properties[0].Value.Deserialize<FindingSeverity>(options)
            : FindingSeverity.Blocking;
    }
}
