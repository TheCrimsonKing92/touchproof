using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DeveloperCompletionStructuralPreflight
{
    internal static DeveloperCompletionStructuralFindings Evaluate(string worktreePath,
        string? projectHomeDirectory = null, string? integrationBranch = null,
        DeveloperCompletionStructuralPhaseTimings? timings = null) =>
        Evaluate(worktreePath, projectHomeDirectory, readBase: integrationBranch is null ? null
            : path => LaneSubstringCollisionBase.Read(path, integrationBranch), timings);

    internal static DeveloperCompletionStructuralFindings Evaluate(string worktreePath,
        string? projectHomeDirectory, Func<string, LaneSubstringCollisionBase?>? readBase,
        DeveloperCompletionStructuralPhaseTimings? timings = null)
    {
        timings ??= new();
        return timings.Measure("total", () => EvaluateCore(worktreePath, projectHomeDirectory, readBase, timings));
    }

    private static DeveloperCompletionStructuralFindings EvaluateCore(string worktreePath,
        string? projectHomeDirectory, Func<string, LaneSubstringCollisionBase?>? readBase,
        DeveloperCompletionStructuralPhaseTimings timings)
    {
        var ratchet = timings.Measure("ratchet", () => SourceSizeRatchetPreflight.Evaluate(worktreePath));
        var hasSizePolicy = File.Exists(Path.Combine(worktreePath, SourceSizeRatchet.SourcePath));
        var manifestPath = AcceptanceManifestLocator.Resolve(worktreePath, projectHomeDirectory);
        var applicable = hasSizePolicy || File.Exists(manifestPath);
        var complete = ratchet.AuthorityAvailable ? ratchet.EvaluationComplete : !hasSizePolicy;
        var unavailable = complete ? null : ratchet.AuthorityAvailable
            ? $"{SourceSizeRatchet.SourcePath}: incomplete authority or unreadable measurements. " +
                string.Join(Environment.NewLine, ratchet.Violations.Select(v => v.Message))
            : $"{SourceSizeRatchet.SourcePath}: source size authority could not be read or parsed.";
        if (ratchet.HasBlockingViolation)
        {
            return new DeveloperCompletionStructuralFindings(true, ratchet.Message, complete, applicable, unavailable, RetryCorrectionCondition.SourceSizeRatchet);
        }

        AcceptanceGateEngineSettings settings;
        try
        {
            settings = timings.Measure("settings-load", () => AcceptanceGateEngineSettings.Load(worktreePath, projectHomeDirectory));
        }
        catch (InvalidDataException exception)
        {
            return new DeveloperCompletionStructuralFindings(
                true,
                $"{AcceptanceManifestDisplayPathResolver.Resolve(worktreePath, projectHomeDirectory)}: {exception.Message}",
                EvaluationComplete: false);
        }

        try
        {
            var violations = EvaluateTestCollectionGuards(worktreePath, settings.InfrastructureTestLanes,
                projectHomeDirectory, readBase, timings, out var guardsComplete, out var guardUnavailable);
            complete &= guardsComplete;
            if (!guardsComplete)
                unavailable = string.Join(Environment.NewLine, new[] { unavailable, guardUnavailable, violations }
                    .Where(message => !string.IsNullOrWhiteSpace(message)));
            if (violations is not null)
                return new DeveloperCompletionStructuralFindings(true, violations, complete, applicable, unavailable);
        }
        catch (InvalidDataException exception)
        {
            return new DeveloperCompletionStructuralFindings(true, $"test collection guard violations: {exception.Message}",
                EvaluationComplete: false);
        }

        return new DeveloperCompletionStructuralFindings(
            false,
            "source size ratchet and acceptance manifest lane consistency checks passed", complete, applicable, unavailable);
    }

    private static string? EvaluateTestCollectionGuards(string worktreePath, IReadOnlyList<AcceptanceTestLane> lanes,
        string? projectHomeDirectory, Func<string, LaneSubstringCollisionBase?>? readBase,
        DeveloperCompletionStructuralPhaseTimings timings, out bool evaluationComplete,
        out string? unavailableDiagnostic)
    {
        evaluationComplete = true;
        unavailableDiagnostic = null;
        if (!Directory.Exists(Path.Combine(worktreePath, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests")) ||
            !lanes.Any(lane => lane.Name == TestCollectionGuardRules.DotnetBuildSlotsLaneName))
            return null;

        var violations = new List<TestCollectionGuardViolation>();
        IReadOnlyDictionary<string, string>? exemptions = null;
        try
        {
            exemptions = timings.Measure("exemptions-load", () => TestCollectionGuardRules.LoadIsolatedCollectionExemptions(worktreePath));
        }
        catch (InvalidDataException exception)
        {
            evaluationComplete = false;
            violations.Add(new TestCollectionGuardViolation(TestCollectionGuardRules.IsolatedCollectionRule,
                "", null, [], [], exception.Message));
        }

        var descriptors = timings.Measure("test-class-scan", () => AcceptanceTestClassSourceScanner.Scan(worktreePath));
        violations.AddRange(timings.Measure("lane-membership", () => TestCollectionGuardRules.FindLaneViolations(lanes, descriptors)));
        if (readBase is not null && string.Equals(
                Path.GetFullPath(AcceptanceManifestLocator.Resolve(worktreePath, projectHomeDirectory)),
                Path.GetFullPath(Path.Combine(worktreePath, "config", "acceptance-manifest.json")),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            var baseline = timings.Measure("baseline-read", () => readBase(worktreePath));
            AcceptanceGateEngineSettings? baseSettings = null;
            try
            {
                if (baseline is not null)
                    baseSettings = timings.Measure("baseline-settings", () => AcceptanceGateEngineSettings.Parse(baseline.BaseManifestJson));
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                // Unavailable base evidence must not suppress the existing structural guards.
            }
            if (baseSettings is not null)
                violations.AddRange(timings.Measure("substring-check", () => LaneSubstringCollisionCheck.Find(baseSettings.InfrastructureTestLanes,
                    lanes, descriptors, baseline!.DiffText)));
            else
            {
                evaluationComplete = false;
                unavailableDiagnostic = "config/acceptance-manifest.json: Lane substring comparison baseline could not be read or parsed.";
            }
        }
        if (exemptions is not null)
            violations.AddRange(timings.Measure("lane-isolation", () => TestCollectionGuardRules.FindUnisolatedLaneClasses(lanes, descriptors,
                TestCollectionGuardRules.DotnetBuildSlotsLaneName, TestCollectionGuardRules.DotnetBuildSlotsCollection,
                exemptions.Keys)));
        if (violations.Count == 0) return null;

        // ScanSources reparses the source tree; only pay that cost when feedback needs file paths.
        var sources = violations.Any(item => item.ClassFullName.Length > 0)
            ? timings.Measure("violation-source-reparse", () => AcceptanceTestClassSourceScanner.ScanSources(worktreePath, includeAncestorSources: false)
                .ToDictionary(item => item.FullName, StringComparer.Ordinal))
            : null;
        var entries = violations.Select(item => new
        {
            Violation = item,
            Path = item.ClassFullName.Length == 0 ? TestCollectionGuardRules.ExemptionsRelativePath
                : sources![item.ClassFullName].SourcePaths.First()
        }).OrderBy(item => item.Violation.Rule, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Violation.ClassFullName, StringComparer.Ordinal);
        return "test collection guard violations: " + string.Join("; ", entries.Select(item =>
            $"{item.Path}: {item.Violation.ClassFullName} [{item.Violation.Rule}] {item.Violation.RepairHint}"));
    }
}

internal sealed record DeveloperCompletionStructuralFindings(bool HasViolation, string Message,
    bool EvaluationComplete = true, bool IsApplicable = true, string? UnavailableDiagnostic = null,
    RetryCorrectionCondition? FailedCondition = null)
{
    internal RetryCorrectionObligation? CorrectionFor(string? candidateSha) =>
        FailedCondition == RetryCorrectionCondition.SourceSizeRatchet &&
        candidateSha is { Length: 40 or 64 } && candidateSha.All(Uri.IsHexDigit)
            ? RetryCorrectionObligation.ForSourceSizeRatchet(candidateSha) : null;
}
