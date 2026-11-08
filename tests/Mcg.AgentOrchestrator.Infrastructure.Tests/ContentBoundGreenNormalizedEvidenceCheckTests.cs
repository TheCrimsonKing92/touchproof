using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static PreTesterNormalizedResultEvidenceTests;
using static RustNextestFocusedEvidenceTests;

// Parallel-safe: each case owns all result artifacts and filesystem mutations.
public sealed class ContentBoundGreenNormalizedEvidenceCheckTests
{
    [Theory]
    [InlineData("passing", null)]
    [InlineData("invalid-paths", "normalized-paths-invalid")]
    [InlineData("relative-path", "normalized-paths-invalid")]
    [InlineData("missing-artifacts", "normalized-artifacts-missing")]
    [InlineData("extra-artifact", "normalized-path-set-mismatch")]
    [InlineData("missing-artifact", "normalized-path-set-mismatch")]
    [InlineData("wrong-kind", "normalized-path-set-mismatch")]
    [InlineData("missing-file", "normalized-content-hash-mismatch")]
    [InlineData("hash-mismatch", "normalized-content-hash-mismatch")]
    [InlineData("length-mismatch", "normalized-content-hash-mismatch")]
    [InlineData("count-missing", "executed-count-missing")]
    [InlineData("count-mismatch", "executed-count-mismatch")]
    [InlineData("no-selections", "no-required-classes")]
    [InlineData("missing-selection", "required-class-missing")]
    [InlineData("failed", "normalized-test-failed")]
    [InlineData("error", "normalized-test-failed")]
    [InlineData("skipped", "normalized-test-skipped")]
    [InlineData("malformed", "normalized-content-invalid")]
    [InlineData("schema", "normalized-content-invalid")]
    [InlineData("report-format", "normalized-content-invalid")]
    [InlineData("duplicate-id", "normalized-content-invalid")]
    [InlineData("duplicate-property", "normalized-content-invalid")]
    [InlineData("missing-name", "normalized-content-invalid")]
    [InlineData("invalid-status", "normalized-content-invalid")]
    [InlineData("invalid-tests", "normalized-content-invalid")]
    [InlineData("missing-tool", "normalized-content-invalid")]
    public void FirstFailure_InvalidEvidence_ReportsSpecificReason(string scenario, string? reason)
    {
        using var fixture = new NormalizedFixture();
        var json = JsonNode.Parse(File.ReadAllText(fixture.ResultPath))!;
        var tests = json["results"]!["tests"]!.AsArray();
        switch (scenario)
        {
            case "schema": json["schemaVersion"] = "mcg-test-result/2"; break;
            case "report-format": json["reportFormat"] = "other"; break;
            case "duplicate-id": tests[1]!["testId"] = tests[0]!["testId"]!.GetValue<string>(); break;
            case "missing-name": tests[1]!.AsObject().Remove("name"); break;
            case "failed": tests[1]!["status"] = "failed"; break;
            case "error": tests[1]!["status"] = "error"; break;
            case "skipped": tests[1]!["status"] = "skipped"; break;
            case "invalid-status": tests[1]!["status"] = "unknown"; break;
            case "invalid-tests": json["results"]!["tests"] = new JsonObject(); break;
            case "missing-tool": json["results"]!.AsObject().Remove("tool"); break;
        }
        var text = json.ToJsonString();
        if (scenario == "malformed") text = "not json";
        if (scenario == "duplicate-property") text = text.Replace("\"status\":\"passed\"",
            "\"status\":\"failed\",\"status\":\"passed\"", StringComparison.Ordinal);
        File.WriteAllText(fixture.ResultPath, text);
        IReadOnlyList<string> paths = [fixture.ResultPath];
        IReadOnlyList<AcceptanceCohortEvidenceArtifact> artifacts = [fixture.Artifact()];
        IReadOnlyCollection<string> selections = [ModuleTest, IntegrationTest];
        var count = scenario == "skipped" ? 1 : 2;
        switch (scenario)
        {
            case "invalid-paths": paths = []; break;
            case "relative-path": paths = ["nextest.ctrf.json"]; break;
            case "missing-artifacts": artifacts = []; break;
            case "extra-artifact": artifacts = [artifacts[0], artifacts[0]]; break;
            case "missing-artifact": paths = new[] { fixture.ResultPath, fixture.ResultPath + ".other" }
                .Order(StringComparer.OrdinalIgnoreCase).ToArray(); break;
            case "wrong-kind": artifacts = [artifacts[0] with { Kind = "trx" }]; break;
            case "missing-file": File.Delete(fixture.ResultPath); break;
            case "hash-mismatch": artifacts = [artifacts[0] with { Sha256 = new string('0', 64) }]; break;
            case "length-mismatch": artifacts = [artifacts[0] with { Length = artifacts[0].Length + 1 }]; break;
            case "count-missing": count = 0; break;
            case "count-mismatch": count = 3; break;
            case "no-selections": selections = []; break;
            case "missing-selection": selections = [ModuleTest, IntegrationTest, "absent::test"]; break;
        }

        var failure = ContentBoundGreenNormalizedEvidenceCheck.FirstFailure(paths, artifacts, count, selections);

        Assert.Equal(reason, failure?.Check);
        if (reason == "required-class-missing") Assert.Equal(["absent::test"], failure!.MissingClasses);
        else if (failure is not null) Assert.Empty(failure.MissingClasses);
    }

    [Fact]
    public void FirstFailure_RepeatedIdAcrossArtifacts_RejectsDoubleCounting()
    {
        using var fixture = new NormalizedFixture();
        var second = fixture.ResultPath + ".second";
        File.Copy(fixture.ResultPath, second);
        var artifact = fixture.Artifact();
        var artifacts = new[] { artifact, artifact with { Path = second } }
            .OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToArray();

        var failure = ContentBoundGreenNormalizedEvidenceCheck.FirstFailure(
            artifacts.Select(value => value.Path).ToArray(), artifacts, 4, [ModuleTest, IntegrationTest]);

        Assert.Equal("normalized-content-invalid", failure?.Check);
    }

    [Fact]
    public void FirstFailure_SkippedDuplicateNameWithPassingRow_RejectsSelection()
    {
        using var fixture = new NormalizedFixture();
        fixture.Write(NormalizedFixture.Row(ModuleTest), NormalizedFixture.Row(IntegrationTest),
            NormalizedFixture.Row(ModuleTest, "other-binary::" + ModuleTest) with { Status = TestResultStatus.Skipped });

        var failure = ContentBoundGreenNormalizedEvidenceCheck.FirstFailure(
            [fixture.ResultPath], [fixture.Artifact()], 2, [ModuleTest, IntegrationTest]);

        Assert.Equal("normalized-test-skipped", failure?.Check);
    }

    [Fact]
    public void FirstFailure_DisjointArtifacts_AggregatesCountAndSelectionCoverage()
    {
        using var fixture = new NormalizedFixture();
        fixture.Write(NormalizedFixture.Row(ModuleTest));
        var second = fixture.ResultPath + ".second";
        File.Copy(fixture.ResultPath, second);
        var firstArtifact = fixture.Artifact() with { Path = second };
        fixture.Write(NormalizedFixture.Row(IntegrationTest));
        var artifacts = new[] { firstArtifact, fixture.Artifact() }
            .OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToArray();

        var failure = ContentBoundGreenNormalizedEvidenceCheck.FirstFailure(
            artifacts.Select(value => value.Path).ToArray(),
            artifacts.Append(new("log", "non-result.log", "", 0)).ToArray(),
            2, [ModuleTest, IntegrationTest]);

        Assert.Null(failure);
    }
}
