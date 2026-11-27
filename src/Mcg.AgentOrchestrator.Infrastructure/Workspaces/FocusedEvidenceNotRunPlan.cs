using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using FocusedEvidenceFilterToken = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilterToken;
using FocusedEvidenceTokenKind = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceTokenKind;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A recorded result establishes execution only when that check ran tests.
internal sealed record FocusedEvidenceNotRunPlan(IReadOnlyList<FocusedEvidenceNotRunPlan.NotRunCheck> Checks,
    IReadOnlyList<string>? Skipped = null)
{
    internal const string ApparatusStoppedAfterFailure = "apparatus-stopped-after-failure";
    internal const string NoResultRecorded = "no-result-recorded";
    internal const string ExecutionNotDemonstrated = "execution-not-demonstrated";
    internal const string NoMatchingTest = "no-matching-test";
    private static readonly Regex ReceiptClassName = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    internal sealed record NotRunCheck(string CheckName, string Reason, IReadOnlyList<FindingEvidenceSelection> Selections,
        IReadOnlyList<FindingEvidenceSelection>? UndemonstratedSelections = null,
        IReadOnlyDictionary<FindingEvidenceSelection, FocusedEvidenceUnexecutedClassCause>? Causes = null);

    internal IReadOnlyList<FindingEvidenceSelection> Selections => Checks.SelectMany(check => check.Selections)
        .DistinctBy(selection => $"{selection.TestProject}:{selection.TestClass}", StringComparer.Ordinal).ToArray();

    internal static FocusedEvidenceNotRunPlan Compare(
        IReadOnlyList<AcceptanceManifestCheck> planned,
        IReadOnlyList<AcceptanceCheckResult> results,
        FocusedEvidenceCoverage coverage,
        bool continueAfterFailure,
        string? worktreePath = null)
    {
        var missing = new List<NotRunCheck>();
        var resultsByName = results.ToLookup(result => result.Name, StringComparer.Ordinal);
        var targetsByCheck = coverage.TargetToChecks
            .SelectMany(target => target.CheckNames.Select(checkName => (checkName, target)))
            .ToLookup(item => item.checkName, item => item.target, StringComparer.Ordinal);
        var normalizedSource = new NormalizedTestResultSource();
        var trxSource = new TrxTestResultSource();
        var executedByPath = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var stoppedAfterFailure = false;
        foreach (var check in planned)
        {
            var recorded = resultsByName[check.Name].ToArray();
            var reason = recorded.Length > 0 ? ExecutionNotDemonstrated
                : stoppedAfterFailure ? ApparatusStoppedAfterFailure : NoResultRecorded;
            stoppedAfterFailure |= !continueAfterFailure && recorded.Any(result => !result.Passed);
            if (RustNextestFocusedEvidence.IsEvidenceCheck(check))
            {
                missing.AddRange(RustNextestFocusedEvidence.NotRunChecks(check, recorded, coverage, reason));
                continue;
            }

            var crosswalk = CrosswalkFocusedEvidence.IsEvidenceCheck(check);
            var go = GoPackageFocusedEvidence.IsEvidenceCheck(check);
            var targets = targetsByCheck[check.Name];
            var reportedExecution = recorded.Length > 0 && recorded.All(result => result.ExecutedTestCount is > 0);
            var hasClassSelection = !crosswalk && !go && (check.IsFocusedEvidenceSelection ||
                check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) ||
                DeclaredTestProjectInventory.IsDeclaredFocusedEvidenceProject(check.Project) ||
                targets.Any(target => target.CheckNames.SelectMany(name => resultsByName[name])
                    .Any(result => result.NormalizedTestResultPaths is { Count: > 0 })));
            if (hasClassSelection)
            {
                var selectedTokens = check.FocusedEvidenceTokens
                    .Where(token => token.Kind is FocusedEvidenceTokenKind.Class or FocusedEvidenceTokenKind.Method)
                    .ToArray();
                var selected = targets.SelectMany(target => ClassSelections(check, target, selectedTokens)).ToArray();
                if (selected.Length > 0)
                {
                    // One requested target can span multiple checks. Only results mapped to that
                    // same target can demonstrate one of its selectors.
                    var remainder = selected.GroupBy(item => item.Target)
                        .SelectMany(group =>
                        {
                            var executed = group.Key.CheckNames.SelectMany(name => resultsByName[name])
                                .SelectMany(ResultIdentities).ToArray();
                            return group.Where(item => !executed.Any(identity =>
                                GoalAcceptanceVerifierTestTelemetry.IsFocusedEvidenceIdentityMatch(identity, item.Value)));
                        }).ToArray();
                    if (remainder.Length > 0)
                    {
                        var receiptSelections = remainder.GroupBy(item => item.Target)
                            .SelectMany(group => ReceiptSelections(group.Key, group.Select(item => item.Selection)))
                            .Distinct().ToArray();
                        var undemonstratedClasses = remainder.DistinctBy(item => item.Selection).ToArray();
                        var causes = undemonstratedClasses.ToDictionary(item => item.Selection, item =>
                            FocusedEvidenceUnexecutedClassCause.Classify(worktreePath, item.Selection.TestProject,
                                item.ContainingClass, check.Project));
                        missing.Add(new(check.Name, $"not-run:{reason}", receiptSelections,
                            undemonstratedClasses.Select(item => item.Selection).ToArray(), causes));
                    }
                    continue;
                }
            }

            if (!crosswalk && reportedExecution)
                continue;

            var selections = targets
                .Select(target => crosswalk
                    ? CrosswalkFocusedEvidence.SplitTarget(check, target.Target)
                    : target.Target.Split(':', 2, StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2)
                .Select(parts => new FindingEvidenceSelection(parts[0], parts[1])).ToArray();
            var undemonstrated = crosswalk
                ? CrosswalkFocusedEvidence.UndemonstratedSelections(check, recorded, selections) : null;
            if (crosswalk && (reportedExecution || selections.Length > 0) && undemonstrated!.Count == 0)
                continue;

            missing.Add(new(check.Name, $"not-run:{reason}", crosswalk ? undemonstrated! : selections,
                go ? GoPackageFocusedEvidence.UndemonstratedSelections(check, recorded, selections) : undemonstrated));
        }
        return new(missing, results.SelectMany(result => result.SkippedTestIdentities ?? [])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

        IEnumerable<string> ResultIdentities(AcceptanceCheckResult result)
        {
            var normalizedPaths = result.NormalizedTestResultPaths ?? [];
            var identities = normalizedPaths.SelectMany(path => ExecutedIdentities(path, normalizedSource));
            if (result.NormalizedTestResultWriteFailure is null)
                return identities;

            // A failed conversion can leave readable execution evidence in the retained TRX.
            return identities.Concat((result.TestResultPaths ?? [])
                .Where(path => !normalizedPaths.Contains(NormalizedTestResultWriter.PathFor(path), StringComparer.Ordinal))
                .SelectMany(path => ExecutedIdentities(path, trxSource)));
        }

        string[] ExecutedIdentities(string path, ITestResultSource source)
        {
            if (!executedByPath.TryGetValue(path, out var identities))
            {
                var read = source.Read(path);
                identities = read.Status == TestResultReadStatus.Readable
                    ? read.Records.Where(record => record.Status != TestResultStatus.Skipped).Select(record => record.Id).ToArray()
                    : [];
                executedByPath.Add(path, identities);
            }
            return identities;
        }
    }

    private static IEnumerable<(FocusedEvidenceTargetCoverage Target, FindingEvidenceSelection Selection, string Value, string ContainingClass)>
        ClassSelections(AcceptanceManifestCheck check, FocusedEvidenceTargetCoverage target, IReadOnlyList<FocusedEvidenceFilterToken> selectedTokens)
    {
        var parts = target.Target.Split(':', 2, StringSplitOptions.TrimEntries);
        var project = parts.Length == 2 ? parts[0] : check.Project;
        if (string.IsNullOrWhiteSpace(project))
            yield break;

        foreach (var part in parts[^1].Split(['|', '&', '(', ')'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var value = FocusedEvidenceUnexecutedClassCause.SelectorValue(part);
            if (value.Length == 0 || !value.All(character => char.IsLetterOrDigit(character) || character is '_' or '.'))
                continue;

            var token = selectedTokens.FirstOrDefault(token => token.Value.Equals(value, StringComparison.OrdinalIgnoreCase) ||
                token.OriginalToken.Trim().Equals(part, StringComparison.OrdinalIgnoreCase));
            if (token is not null || selectedTokens.Count == 0)
                yield return (target, new(project, part), token?.Value ?? value, token?.ContainingClass ?? value);
        }
    }

    private static IEnumerable<FindingEvidenceSelection> ReceiptSelections(
        FocusedEvidenceTargetCoverage target, IEnumerable<FindingEvidenceSelection> undemonstrated)
    {
        var filter = target.Target.Split(':', 2, StringSplitOptions.TrimEntries)[^1];
        // FindingEvidenceRoundPlanner.TryExpandClasses expands only plain class alternatives.
        // The receipt must retain the full filter for every request that consumer keeps whole.
        return IsReceiptClassSet(filter) ? undemonstrated : [new(undemonstrated.First().TestProject, filter)];
    }

    private static bool IsReceiptClassSet(string filter)
    {
        var depth = 0;
        foreach (var character in filter)
        {
            if (character == '(')
                depth++;
            else if (character == ')' && --depth < 0)
                return false;
        }

        const string prefix = "FullyQualifiedName~";
        return depth == 0 && filter.Split('|', StringSplitOptions.TrimEntries)
            .Select(part => part.Trim('(', ')').Trim())
            .Select(part => part.StartsWith(prefix, StringComparison.Ordinal) ? part[prefix.Length..] : part)
            .All(ReceiptClassName.IsMatch);
    }

    internal string AppendSummary(string summary)
    {
        var lines = Checks.SelectMany(check => new[] { $"{check.CheckName} {check.Reason}" }
                .Concat((check.UndemonstratedSelections ?? []).Select(selection =>
                    $"undemonstrated {selection.TestProject}:{selection.TestClass} {check.Reason}" +
                    (check.Causes?.TryGetValue(selection, out var cause) == true ? $" cause={cause.Label}" : ""))))
            .Concat((Skipped ?? []).Select(identity => $"skipped: {identity}")).ToArray();
        return lines.Length == 0 ? summary : summary + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }
}
