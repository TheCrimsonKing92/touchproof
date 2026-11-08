using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using TestResult = Mcg.AgentOrchestrator.Infrastructure.TestResult;
using static RustNextestFocusedEvidenceTests;

// Parallel-safe: fixtures own their temporary result files; no processes or shared state.
public sealed class PreTesterNormalizedResultEvidenceTests
{
    [Fact]
    public void ClassifyOutcome_TwoPassingRustSelections_DeliversGreenReceipt()
    {
        using var fixture = new NormalizedFixture();
        var result = fixture.Classify();

        Assert.Equal("green", result.Outcome);
        Assert.Null(result.Unusable);
        var receipt = Assert.IsType<FindingEvidenceArmReceipt>(result.Candidate);
        Assert.Empty(receipt.TestResultPaths!);
        Assert.Equal([fixture.ResultPath], receipt.NormalizedTestResultPaths);
        Assert.Contains(fixture.ResultPath, receipt.ReceiptPaths!);
        Assert.Equal(fixture.Artifact(), Assert.Single(receipt.ReceiptArtifacts!));
        Assert.Equal(2, receipt.ExecutedTestCount);
    }

    [Theory]
    [InlineData("missing-selection", "required-class-missing")]
    [InlineData("failed-row", "normalized-test-failed")]
    [InlineData("error-row", "normalized-test-failed")]
    [InlineData("skipped-row", "normalized-test-skipped")]
    [InlineData("wrong-case", "required-class-missing")]
    [InlineData("short-name", "required-class-missing")]
    public void ClassifyOutcome_InvalidRustEvidence_IsUnusable(string mutation, string reason)
    {
        using var fixture = new NormalizedFixture();
        var rows = fixture.Rows;
        switch (mutation)
        {
            case "missing-selection": rows = [rows[0]]; break;
            case "failed-row": rows[1] = rows[1] with { Status = TestResultStatus.Failed }; break;
            case "error-row": rows[1] = rows[1] with { Status = TestResultStatus.Error }; break;
            case "skipped-row": rows[1] = rows[1] with { Status = TestResultStatus.Skipped }; break;
            case "wrong-case": rows[1] = rows[1] with { DisplayName = IntegrationTest.ToUpperInvariant() }; break;
            case "short-name": rows[0] = rows[0] with { DisplayName = ModuleTest.Split("::")[^1] }; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        fixture.Write(rows);

        var result = fixture.Classify(executedCount: rows.Count(row => row.Status != TestResultStatus.Skipped));

        Assert.Equal("unusable", result.Outcome);
        Assert.Equal(reason, Assert.IsType<PreTesterUnusableReason>(result.Unusable).Check);
        if (reason == "required-class-missing")
            Assert.Equal([mutation == "short-name" ? ModuleTest : IntegrationTest], result.Unusable!.MissingClasses);
    }

    [Fact]
    public void ContentBoundFailure_TamperedReceiptHash_ReportsDistinctUnusableReason()
    {
        using var fixture = new NormalizedFixture();
        var receipt = Assert.IsType<FindingEvidenceArmReceipt>(fixture.Classify().Candidate);
        receipt = receipt with
        {
            ReceiptArtifacts = [Assert.Single(receipt.ReceiptArtifacts!) with { Sha256 = new string('0', 64) }]
        };

        var reason = PreTesterApparatusReceiptClassifier.ContentBoundFailure(receipt, [ModuleTest, IntegrationTest]);

        Assert.Equal("normalized-content-hash-mismatch", Assert.IsType<PreTesterUnusableReason>(reason).Check);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("id")]
    [InlineData("suite")]
    public void ClassifyOutcome_CrosswalkSelectors_UsesAdapterIdentityRules(string selectorKind)
    {
        using var fixture = new NormalizedFixture();
        fixture.Write(NormalizedFixture.Row("alpha", "src/one.test.ts::Widget works"),
            NormalizedFixture.Row("beta", "src/two.test.ts::Other works"));
        string[] selectors = selectorKind switch
        {
            "file" => ["src/one.test.ts", "src/two.test.ts"],
            "id" => ["src/one.test.ts::Widget works", "src/two.test.ts::Other works"],
            "suite" => ["Widget", "Other"],
            _ => throw new ArgumentOutOfRangeException(nameof(selectorKind))
        };

        var result = fixture.Classify(selectors, checkName: "crosswalk focused evidence: declared-test");

        Assert.Equal("green", result.Outcome);
        Assert.Null(result.Unusable);
    }

    [Fact]
    public void ClassifyOutcome_CrosswalkUncoveredFile_IsUnusable()
    {
        using var fixture = new NormalizedFixture();
        fixture.Write(NormalizedFixture.Row("alpha", "src/one.test.ts::Widget works"));

        var result = fixture.Classify(["src/other.test.ts"], 1, "crosswalk focused evidence: declared-test");

        Assert.Equal("unusable", result.Outcome);
        Assert.Equal("required-class-missing", result.Unusable!.Check);
        Assert.Equal(["src/other.test.ts"], result.Unusable.MissingClasses);
    }

    [Fact]
    public void CreateReceipt_TrxWithSupplementalNormalizedPath_PreservesTrxRepresentation()
    {
        using var fixture = new PreTesterGreenSubclassCoverageTests.TrxFixture("A");
        var arm = Assert.Single(fixture.Evidence.Arms!);
        arm = arm with { Checks = arm.Checks.Select(check => check with
            { NormalizedTestResultPaths = ["supplemental-must-not-be-used"] }).ToArray() };

        var receipt = ConductorDriver.CreateFindingEvidenceArmReceipt(arm);

        Assert.Null(receipt.NormalizedTestResultPaths);
        Assert.All(receipt.ReceiptArtifacts!, artifact => Assert.Equal("trx", artifact.Kind));
        Assert.DoesNotContain("NormalizedTestResultPaths", JsonSerializer.Serialize(receipt));
        Assert.Null(PreTesterApparatusReceiptClassifier.ContentBoundFailure(receipt,
            fixture.Selections.Select(selection => selection.TestClass).ToArray()));
    }

    internal sealed class NormalizedFixture : IDisposable
    {
        private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();
        internal string ResultPath => Path.Combine(_root, "nextest.ctrf.json");
        internal TestResult[] Rows => [Row(ModuleTest), Row(IntegrationTest)];

        internal NormalizedFixture() => Write(Rows);

        internal void Write(params TestResult[] rows) =>
            NormalizedTestResultWriter.Write(ResultPath, "cargo-nextest", rows);

        internal AcceptanceCohortEvidenceArtifact Artifact()
        {
            var bytes = File.ReadAllBytes(ResultPath);
            return new(AcceptanceCohortCheckEvidence.NormalizedFormat, ResultPath,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
        }

        internal (string Outcome, FindingEvidenceArmReceipt? Candidate, IReadOnlyList<string> FailingTests,
            PreTesterUnusableReason? Unusable) Classify(string[]? selectors = null, int executedCount = 2,
            string checkName = "rust nextest focused evidence: declared-test")
        {
            var selections = (selectors ?? [ModuleTest, IntegrationTest])
                .Select(name => new FindingEvidenceSelection("Cargo.toml", name)).ToArray();
            var checks = new[] { new AcceptanceCheckResult(checkName, true, 0, "passed",
                ExecutedTestCount: executedCount) { NormalizedTestResultPaths = [ResultPath] } };
            var evidence = new FocusedEvidenceRunResult("Cargo.toml:tests", true, true, "passed", checks,
                Arms: [new(FindingEvidenceArm.Candidate, "candidate", FindingEvidenceArmDisposition.Green,
                    true, true, "passed", checks)], OutcomeReason: FindingEvidenceOutcomeReason.ValidEvidence);
            return PreTesterApparatusReceiptClassifier.ClassifyOutcome(evidence, true, "candidate", selections, _ => false);
        }

        internal static TestResult Row(string name, string? id = null) => new(id ?? "rust-binary::" + name,
            name, "src/lib.rs", ["suite"], TestResultStatus.Passed, 1, "message", "trace",
            [new(TestResultStatus.Passed, 1, null, null)], "seed", "output", "cargo-nextest");

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
