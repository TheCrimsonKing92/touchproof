using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using GradeResult = Mcg.AgentOrchestrator.Infrastructure.CrosswalkFocusedEvidence.SelectionGrade;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Plans exact Rust test names through the shared JUnit command evidence route.</summary>
internal static class RustNextestFocusedEvidence
{
    private const string FocusedPrefix = "rust nextest focused evidence: ";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex SelectorPattern = new(@"\A[A-Za-z_][A-Za-z0-9_]*(::[A-Za-z_][A-Za-z0-9_]*)*\z");

    internal static Check? ReadDeclaredCheck(JsonElement manifestRoot)
    {
        if (!manifestRoot.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var element in checks.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            try
            {
                var check = element.Deserialize<Check>(JsonOptions);
                if (check is not null &&
                    (check.Type?.Equals("junit-command", StringComparison.OrdinalIgnoreCase) == true ||
                     check.Type?.Equals("command", StringComparison.OrdinalIgnoreCase) == true) &&
                    string.Equals(check.Command, "cargo", StringComparison.OrdinalIgnoreCase) &&
                    check.Arguments is { Count: > 0 } && check.Arguments[0] == "nextest")
                    return check;
            }
            catch (JsonException)
            {
                // Unreadable entries contribute no declaration, as with the other engines.
            }
        }
        return null;
    }

    internal static bool Applies(AcceptanceGateEngineSettings? settings) =>
        settings?.DeclaredRustNextestCheck is not null && settings.DeclaredGoTestCheck is null &&
        settings.DeclaredCrosswalkTestCheck is null && settings.MtpInvocations.Count == 0 &&
        settings.DeclaredDotnetTestChecks.Count == 0;

    internal static bool TryResolveProject(string alias, AcceptanceGateEngineSettings? settings, out string project)
    {
        project = string.Empty;
        if (!Applies(settings) || string.IsNullOrWhiteSpace(alias)) return false;
        project = settings!.DeclaredRustNextestCheck!.Name;
        return true;
    }

    internal static bool IsSupportedSelector(string selector) => SelectorPattern.IsMatch(selector);

    internal static bool TryBuild(
        string request, AcceptanceGateEngineSettings settings, string worktreePath,
        out IReadOnlyList<Check> checks, out FocusedEvidenceCoverage coverage,
        out FocusedEvidenceRejection rejection)
    {
        checks = [];
        coverage = new(TargetToChecks: []);
        rejection = new(FocusedEvidenceRejectionCode.EmptyRequest, request, "empty evidence request");
        if (!Applies(settings)) return false;
        var items = request.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(item => item.TrimStart()).ToArray();
        if (items.Length == 0) return false;
        rejection = new(FocusedEvidenceRejectionCode.UnsupportedToken, request,
            "Rust nextest evidence requires bounded test names, a nextest run invocation using the ci profile, and one relative JUnit report.");
        var declared = settings.DeclaredRustNextestCheck!;
        var selectors = new List<string>();
        foreach (var item in items)
        {
            var parts = SplitTarget(declared.Name, item);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || !IsSupportedSelector(parts[1]))
                return false;
            selectors.Add(parts[1]);
        }
        if (declared.Reports is not { Count: 1 } || !JunitCommandCheckDefinition.IsRelativePath(declared.Reports[0]) ||
            !JunitCommandCheckDefinition.IsRelativePath(declared.Project ?? ".") ||
            !TryBuildArguments(declared.Arguments, selectors, out var arguments))
            return false;
        var check = new Check
        {
            Name = FocusedPrefix + declared.Name, Type = "junit-command", Command = declared.Command,
            Project = declared.Project, Arguments = arguments, Reports = declared.Reports,
            Environment = declared.Environment ?? new Dictionary<string, string>(), TimeoutMinutes = declared.TimeoutMinutes,
            Advisory = declared.Advisory, IsFocusedEvidenceSelection = true
        };
        checks = [check];
        coverage = new(items.Select(item => new FocusedEvidenceTargetCoverage(item, [check.Name])).ToArray(),
            ExecutionMode: "focused", ExecutionReason: "rust-nextest-focused");
        return true;
    }

    private static bool TryBuildArguments(IReadOnlyList<string> declared, IReadOnlyList<string> selectors,
        out IReadOnlyList<string> arguments)
    {
        arguments = [];
        if (declared.Count < 2 || declared[1] != "run") return false;
        var profileSeen = false;
        for (var index = 2; index < declared.Count; index++)
        {
            var argument = declared[index];
            if (argument is null || argument == "--" || argument.StartsWith("-E", StringComparison.Ordinal) ||
                argument is "--filter-expr" or "--filterset" ||
                argument.StartsWith("--filter-expr=", StringComparison.Ordinal) ||
                argument.StartsWith("--filterset=", StringComparison.Ordinal)) return false;
            if (argument is "--profile" or "-P")
            {
                if (profileSeen || ++index >= declared.Count || declared[index] != "ci") return false;
                profileSeen = true;
            }
            else if (argument.StartsWith("--profile=", StringComparison.Ordinal) || argument.StartsWith("-P", StringComparison.Ordinal))
            {
                if (profileSeen || argument != "--profile=ci") return false;
                profileSeen = true;
            }
        }
        arguments = declared.Concat(profileSeen ? Array.Empty<string>() : ["--profile", "ci"])
            .Concat(["-E", string.Join("|", selectors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(selector => $"test(={selector})"))]).ToArray();
        return true;
    }

    internal static bool IsEvidenceCheck(Check check) =>
        check.Type.Equals("junit-command", StringComparison.OrdinalIgnoreCase) &&
        check.Name.StartsWith(FocusedPrefix, StringComparison.Ordinal);

    internal static string[] SplitTarget(Check check, string target) => SplitTarget(check.Name[FocusedPrefix.Length..], target);

    private static string[] SplitTarget(string declaredName, string target)
    {
        var label = GoalAcceptanceVerifier.ProjectLabel(declaredName);
        var prefix = label + ":";
        if (target.StartsWith(prefix, StringComparison.Ordinal)) return [label, target[prefix.Length..]];
        for (var index = 0; index < target.Length; index++)
        {
            if (target[index] != ':') continue;
            if (index + 1 < target.Length && target[index + 1] == ':') { index++; continue; }
            return [target[..index].Trim(), target[(index + 1)..]];
        }
        return [];
    }

    internal static GradeResult Grade(IEnumerable<TestResult> records, string selection) =>
        CrosswalkFocusedEvidence.Grade(records.Where(record => record.DisplayName == selection)
            .Select(record => record with { Id = selection }), selection);

    internal static IReadOnlyList<FocusedEvidenceNotRunPlan.NotRunCheck> NotRunChecks(
        Check check, IReadOnlyList<AcceptanceCheckResult> recorded, FocusedEvidenceCoverage coverage,
        string fallbackReason)
    {
        var selections = coverage.TargetToChecks.Where(target => target.CheckNames.Contains(check.Name, StringComparer.Ordinal))
            .Select(target => SplitTarget(check, target.Target)).Where(parts => parts.Length == 2)
            .Select(parts => new FindingEvidenceSelection(parts[0], parts[1])).Distinct().ToArray();
        var source = new NormalizedTestResultSource();
        var receipts = recorded.SelectMany(result => result.NormalizedTestResultPaths ?? [])
            .Distinct(StringComparer.Ordinal).Select(source.Read).ToArray();
        var readable = receipts.Length > 0 && receipts.All(receipt => receipt.Status == TestResultReadStatus.Readable);
        var rows = receipts.Where(receipt => receipt.Status == TestResultReadStatus.Readable)
            .SelectMany(receipt => receipt.Records).ToArray();
        var noTests = rows.Length == 0 && receipts.Length == 0 && recorded.Count > 0 && recorded.All(result => result.ExitCode == 4 && result.ExecutedTestCount == 0 &&
            result.CrosswalkDiagnostics is { Count: > 0 } diagnostics &&
            diagnostics.All(diagnostic => diagnostic.Category is CrosswalkDiagnosticCategory.NonzeroExit or CrosswalkDiagnosticCategory.NoExecution));
        var missing = new List<FindingEvidenceSelection>();
        var undemonstrated = new List<FindingEvidenceSelection>();
        foreach (var selection in selections)
        {
            if ((readable && !rows.Any(row => row.DisplayName == selection.TestClass)) || noTests)
                missing.Add(selection);
            else if (!readable || Grade(rows, selection.TestClass) == GradeResult.NotDemonstrated)
                undemonstrated.Add(selection);
        }
        var checks = new List<FocusedEvidenceNotRunPlan.NotRunCheck>();
        if (missing.Count > 0)
            checks.Add(new(check.Name, "not-run:" + FocusedEvidenceNotRunPlan.NoMatchingTest, missing, missing));
        if (undemonstrated.Count > 0)
            checks.Add(new(check.Name, "not-run:" + fallbackReason, undemonstrated, undemonstrated));
        return checks;
    }
}
