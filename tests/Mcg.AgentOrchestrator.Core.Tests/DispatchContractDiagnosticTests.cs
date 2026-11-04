using Mcg.AgentOrchestrator.Core;

public sealed class DispatchContractDiagnosticTests
{
    private const string RejectionRule = DispatchFailureDiagnosticMarker.ResearcherOutputContractRejected;
    private const string Diagnostic = "Researcher output contract failed: missing required section 'likely seams and risks'. Retry Planner for contract repair.";

    [Xunit.Theory]
    [Xunit.InlineData(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing, DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing, "\n")]
    [Xunit.InlineData(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing, DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing, "\r\n")]
    [Xunit.InlineData(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed, DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed, "\n")]
    [Xunit.InlineData(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed, DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed, "\r\n")]
    [Xunit.InlineData(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing, DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed, "\n")]
    [Xunit.InlineData(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing, DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed, "\r\n")]
    [Xunit.InlineData(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed, DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing, "\n")]
    [Xunit.InlineData(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed, DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing, "\r\n")]
    public void ExtractsOnlyAdjacentReasonsAfterLastHostBuildMarker(string rule, string hostRule, string newline)
    {
        var marker = DispatchFailureDiagnosticMarker.Format(rule);
        const string expected = "worker_build_applicability=tree-mismatch; next=supply evidence matching the current candidate";
        const string second = "worker_build_applicability=incomplete-project-coverage; next=supply one invocation covering all required projects";
        var workerLines = Enumerable.Repeat("worker_build_applicability=unsupported-worker-reason; next=worker-authored", 9);
        var stderr = string.Join(newline, new[] { marker }.Concat(workerLines).Concat(new[]
        {
            "worker noise", DispatchFailureDiagnosticMarker.Format(hostRule), expected, second, "applicability_current: dispatch=current",
            "worker_build_applicability=outside-explanation; next=unsupported", "later accounting"
        }));

        Xunit.Assert.True(DispatchContractDiagnostic.TryExtract(stderr, rule, out var diagnostic));
        Xunit.Assert.Equal(expected + " | " + second, diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing, DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing)]
    [Xunit.InlineData(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed, DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed)]
    [Xunit.InlineData(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing, DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed)]
    [Xunit.InlineData(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed, DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing)]
    public void RejectsWorkerReasonsWithoutAdjacentHostExplanation(string rule, string hostRule)
    {
        const string reason = "worker_build_applicability=unsupported-worker-reason; next=worker-authored";
        var marker = DispatchFailureDiagnosticMarker.Format(rule);
        var hostMarker = DispatchFailureDiagnosticMarker.Format(hostRule);
        foreach (var stderr in new[] { reason, $"{marker}\n{reason}\nworker noise\n{hostMarker}",
            $"{marker}\nworker noise\n{reason}",
            $"{marker}\n{reason}\nworker noise\n{hostMarker}\nworker noise\n{reason}" })
        {
            Xunit.Assert.False(DispatchContractDiagnostic.TryExtract(stderr, rule, out var diagnostic));
            Xunit.Assert.Equal(string.Empty, diagnostic);
        }
    }

    [Xunit.Fact]
    public void ExtractsBoundedReasonsAfterConsecutiveHostBuildMarkers()
    {
        var markers = DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed) + "\n" +
            DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing);
        var reasons = Enumerable.Range(0, 10).Select(i => $"worker_build_applicability=reason-{i}; next=inspect").ToArray();
        var stderr = markers + "\n" + string.Join("\n", reasons);
        foreach (var rule in new[] { DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed, DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing })
        {
            Xunit.Assert.True(DispatchContractDiagnostic.TryExtract(stderr, rule, out var diagnostic));
            Xunit.Assert.Equal(string.Join(" | ", reasons.Take(9)), diagnostic);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("\n")]
    [Xunit.InlineData("\r\n")]
    public void ExtractsContractDiagnosticWithEitherLineEnding(string newline)
    {
        var stderr = string.Join(newline,
            "ordinary worker noise", Diagnostic, DispatchFailureDiagnosticMarker.Format(RejectionRule),
            "later resource accounting");

        Xunit.Assert.True(DispatchContractDiagnostic.TryExtract(stderr, RejectionRule, out var diagnostic));
        Xunit.Assert.Equal(Diagnostic, diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData(DispatchFailureDiagnosticMarker.ResearcherArtifactPersistenceFailed,
        "Scout research output contract could not persist the accepted artifact: write failed.")]
    [Xunit.InlineData(RejectionRule,
        "Researcher durable receipt failed revalidation: missing required section 'likely seams and risks'. Retry Researcher for contract repair.")]
    public void ExtractsOtherOrchestratorContractPrefixes(string rule, string expected)
    {
        var stderr = $"worker noise\n{expected}\n{DispatchFailureDiagnosticMarker.Format(rule)}";

        Xunit.Assert.True(DispatchContractDiagnostic.TryExtract(stderr, rule, out var diagnostic));
        Xunit.Assert.Equal(expected, diagnostic);
    }

    [Xunit.Fact]
    public void UsesLastMatchingMarkerAndNearestNonBlankLineTrimmed()
    {
        const string latest = "Researcher output contract failed: required section 'likely seams and risks' is not substantive.";
        var marker = DispatchFailureDiagnosticMarker.Format(RejectionRule);
        var stderr = $"{Diagnostic}\n{marker}\nworker noise\n \t{latest}\t \n \t\n \t{marker}\t \nresource receipt";

        Xunit.Assert.True(DispatchContractDiagnostic.TryExtract(stderr, RejectionRule, out var diagnostic));
        Xunit.Assert.Equal(latest, diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData(" \t\r\n")]
    [Xunit.InlineData(Diagnostic)]
    public void RejectsMissingMarker(string? stderr)
    {
        Xunit.Assert.False(DispatchContractDiagnostic.TryExtract(stderr, RejectionRule, out var diagnostic));
        Xunit.Assert.Equal(string.Empty, diagnostic);
    }

    [Xunit.Fact]
    public void RejectsMarkerForDifferentRule()
    {
        var stderr = $"{Diagnostic}\n{DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.ResearcherArtifactPersistenceFailed)}";

        Xunit.Assert.False(DispatchContractDiagnostic.TryExtract(stderr, RejectionRule, out var diagnostic));
        Xunit.Assert.Equal(string.Empty, diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("ordinary worker output")]
    [Xunit.InlineData("researcher output contract failed: worker-authored lowercase line")]
    [Xunit.InlineData("")]
    public void RejectsNonContractLineBeforeLastMarkerWithoutFallingBack(string line)
    {
        var marker = DispatchFailureDiagnosticMarker.Format(RejectionRule);
        var stderr = line.Length == 0
            ? $" \t\n{marker}"
            : $"{Diagnostic}\n{marker}\n{line}\n{marker}";

        Xunit.Assert.False(DispatchContractDiagnostic.TryExtract(stderr, RejectionRule, out var diagnostic));
        Xunit.Assert.Equal(string.Empty, diagnostic);
    }

    [Xunit.Fact]
    public void RejectsPlannerRuleEvenWithMatchingMarkerAndContractPrefix()
    {
        const string rule = DispatchFailureDiagnosticMarker.PlannerOutputContractRejected;
        var stderr = $"{Diagnostic}\n{DispatchFailureDiagnosticMarker.Format(rule)}";

        Xunit.Assert.False(DispatchContractDiagnostic.TryExtract(stderr, rule, out var diagnostic));
        Xunit.Assert.Equal(string.Empty, diagnostic);
    }
}
