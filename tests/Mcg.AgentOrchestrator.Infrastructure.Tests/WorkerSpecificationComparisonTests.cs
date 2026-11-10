using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerSpecificationComparisonTests(Xunit.ITestOutputHelper output)
{
    // These knobs are read only by this test host. Baseline capture uses this unchanged file
    // against actual main, not a second implementation of the old renderer.
    [Xunit.Fact]
    public void RetainPackagesAndCompareWithSuppliedMainBaseline()
    {
        var destination = Environment.GetEnvironmentVariable("WORKER_SPECIFICATION_REPORT_DIRECTORY") ??
            Path.Combine(Path.GetTempPath(), "mcg-specification-comparison", Guid.NewGuid().ToString("N"));
        var baseline = Environment.GetEnvironmentVariable("WORKER_SPECIFICATION_BASELINE_DIRECTORY");
        var revision = Environment.GetEnvironmentVariable("WORKER_SPECIFICATION_SOURCE_REVISION") ?? "unspecified-capture";
        if (baseline is not null) Assert.NotEqual("unspecified-capture", revision);
        Assert.True(Path.IsPathFullyQualified(destination));
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "source-revision.txt"), revision);
        var reports = new List<PackageReport>();
        foreach (var role in new[] { AgentRole.Researcher, AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer })
        foreach (var redundant in new[] { true, false })
        {
            using var fixture = CreateFixture(role, redundant);
            var name = $"{role}-{(redundant ? "redundant" : "compact")}";
            var directory = Path.Combine(destination, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "prompt.md"), fixture.Prompt, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "input.json"), JsonSerializer.Serialize(new
            {
                fixture.Goal.Objective, Spec = fixture.Goal.RefinedSpec, fixture.Task.RequiredRole,
                fixture.Goal.BriefVersions, fixture.Goal.RefinedSpecVersions,
                Candidate = "matched-fixture-candidate", Base = "matched-fixture-main"
            }));
            var sections = new List<SectionReport>();
            foreach (var artifact in fixture.Package.Artifacts)
            {
                var relative = artifact.Identity.Value;
                var path = Path.Combine(directory, "artifacts", relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var bytes = artifact.AuthoritativeBytes!;
                if (artifact.MandatoryRelativePath is { } materialized)
                    Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Root, materialized)));
                Assert.Equal(artifact.ContentHash, WorkerContextArtifact.Hash(bytes));
                File.WriteAllBytes(path, bytes);
                var rendered = artifact.DeliveryMode == ContextDeliveryMode.HistoricalFile ? "" : WorkerContextPackageBuilder.RenderArtifact(artifact);
                File.WriteAllText(path + ".rendered", rendered, new UTF8Encoding(false));
                sections.Add(new(relative, artifact.ContentHash, artifact.DeliveryMode.ToString(), rendered.Length,
                    Encoding.UTF8.GetByteCount(rendered), bytes.Length, artifact.MandatoryRelativePath is null ? 0 : bytes.Length,
                    artifact.RoleVisibility.Select(value => value.ToString()).ToArray(), artifact.ContractVersion.Value));
            }
            var report = new PackageReport(name, fixture.Prompt.Length, Encoding.UTF8.GetByteCount(fixture.Prompt), sections);
            File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report));
            reports.Add(report);
            VerifyManifest(directory, report);
            if (baseline is not null) CompareRetained(Path.Combine(baseline, name), directory, redundant);
        }
        File.WriteAllText(Path.Combine(destination, "reports.json"), JsonSerializer.Serialize(reports));
        output.WriteLine($"Retained comparison: {destination}; UTF-16 code units and UTF-8 bytes, not tokens/cost/quality. Baseline={baseline ?? "capture-only"}");
    }

    private static void CompareRetained(string baseline, string candidate, bool redundant)
    {
        Assert.Equal(File.ReadAllBytes(Path.Combine(baseline, "input.json")), File.ReadAllBytes(Path.Combine(candidate, "input.json")));
        var before = JsonSerializer.Deserialize<PackageReport>(File.ReadAllText(Path.Combine(baseline, "report.json")))!;
        var after = JsonSerializer.Deserialize<PackageReport>(File.ReadAllText(Path.Combine(candidate, "report.json")))!;
        if (redundant)
        {
            Assert.True(after.Utf16 < before.Utf16, "The actual candidate package must be smaller than actual main.");
            Assert.True(after.Utf8 < before.Utf8);
        }
        else
        {
            Assert.Equal(before.Utf16, after.Utf16);
            Assert.Equal(before.Utf8, after.Utf8);
            Assert.Equal(File.ReadAllBytes(Path.Combine(baseline, "prompt.md")), File.ReadAllBytes(Path.Combine(candidate, "prompt.md")));
        }
        var differences = new List<string>();
        VerifyManifest(baseline, before);
        VerifyManifest(candidate, after);
        foreach (var section in before.Sections)
        {
            var current = Assert.Single(after.Sections, item => item.Identity == section.Identity);
            Assert.Equal(section.Roles, current.Roles);
            Assert.Equal(section.ContractVersion, current.ContractVersion);
            if (section.Identity == "context/manifest.v1.json")
            {
                if (section.Hash != current.Hash)
                    differences.Add("context/manifest.v1.json: inventory reflects only verified delivery modes and generated projections");
                continue;
            }
            Assert.Equal(section.Hash, current.Hash); // All pre-existing authoritative artifact bytes.
            if (section.Mode != current.Mode)
            {
                Assert.Contains(section.Identity, new[] { "goal/objective.md", "goal/refined-spec.json" });
                Assert.Equal("OnDemandFile", current.Mode);
                differences.Add($"{section.Identity}: exact source retained; inline -> selective artifact access");
            }
        }
        foreach (var section in after.Sections.Where(item => before.Sections.All(old => old.Identity != item.Identity)))
        {
            Assert.Contains(section.Identity, new[] { "goal/current-objective.md", "goal/current-specification.json", "goal/specification-history.json" });
            differences.Add($"{section.Identity}: authorized generated projection");
        }
        var source = ReadArtifact(candidate, "goal/refined-spec.json");
        if (after.Sections.Any(section => section.Identity == "goal/current-specification.json"))
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(source), Expand(ReadArtifact(candidate, "goal/current-specification.json"))));
        var objective = ReadArtifact(candidate, "goal/objective.md");
        if (after.Sections.Any(section => section.Identity == "goal/current-objective.md"))
        {
            var projected = Encoding.UTF8.GetString(ReadArtifact(candidate, "goal/current-objective.md"));
            var criteria = JsonNode.Parse(source)!["AcceptanceCriteria"]!.AsArray();
            for (var i = 0; i < criteria.Count; i++)
                projected = projected.Replace($"- See current specification, acceptance criterion {i + 1}.", $"{i + 1}. {criteria[i]!.GetValue<string>()}", StringComparison.Ordinal);
            Assert.Equal(Encoding.UTF8.GetString(objective), projected);
        }
        if (after.Sections.Any(section => section.Identity == "goal/specification-history.json"))
        {
            var history = JsonNode.Parse(ReadArtifact(candidate, "goal/specification-history.json"))!;
            var input = JsonNode.Parse(File.ReadAllBytes(Path.Combine(candidate, "input.json")))!;
            Assert.True(JsonNode.DeepEquals(input["BriefVersions"], history["BriefVersions"]));
            Assert.True(JsonNode.DeepEquals(input["RefinedSpecVersions"], history["RefinedSpecVersions"]));
        }
        File.WriteAllText(Path.Combine(candidate, "authorized-differences.json"), JsonSerializer.Serialize(differences));
    }

    private static byte[] ReadArtifact(string directory, string identity) =>
        File.ReadAllBytes(Path.Combine(directory, "artifacts", identity.Replace('/', Path.DirectorySeparatorChar)));

    private static void VerifyManifest(string directory, PackageReport report)
    {
        foreach (var section in report.Sections)
        {
            var bytes = ReadArtifact(directory, section.Identity);
            Assert.Equal(section.Hash, WorkerContextArtifact.Hash(bytes));
            Assert.Equal(section.AuthoritativeBytes, bytes.Length);
            var rendered = File.ReadAllText(Path.Combine(directory, "artifacts", section.Identity.Replace('/', Path.DirectorySeparatorChar)) + ".rendered");
            Assert.Equal(section.Utf16, rendered.Length);
            Assert.Equal(section.Utf8, Encoding.UTF8.GetByteCount(rendered));
        }
        var manifest = JsonNode.Parse(ReadArtifact(directory, "context/manifest.v1.json"))!;
        Assert.Equal(1, manifest["ContractVersion"]!.GetValue<int>());
        Assert.Equal(new[] { "artifact-registry.json", "context-package.json", "manifest.md" },
            manifest["CompatibilityAliases"]!.AsArray().Select(node => node!.GetValue<string>()));
        var entries = manifest["Artifacts"]!.AsArray();
        var expected = report.Sections.Where(section => section.Identity != "context/manifest.v1.json" &&
            section.Mode is not ("OnDemandFile" or "HistoricalFile")).ToArray();
        Assert.Equal(expected.Length, entries.Count);
        foreach (var entry in entries)
        {
            var section = Assert.Single(expected, section => section.Identity == entry!["LogicalIdentity"]!.GetValue<string>());
            Assert.Equal(section.Hash, entry!["Sha256"]!.GetValue<string>());
            Assert.Equal(section.Mode, entry["DeliveryMode"]!.GetValue<string>());
            Assert.Equal(section.Roles, entry["RoleVisibility"]!.AsArray().Select(node => node!.GetValue<string>()));
            Assert.Equal(section.ContractVersion, entry["ContractVersion"]!.GetValue<int>());
            Assert.Equal(section.Hash, WorkerContextArtifact.Hash(ReadArtifact(directory, section.Identity)));
        }
    }

    internal static JsonObject Expand(byte[] bytes)
    {
        var root = JsonNode.Parse(bytes)!.AsObject();
        if (root["SharedBodies"] is not JsonObject bodies) return root;
        foreach (var (collection, property) in new[] { ("Decisions", "Choice"), ("OpenQuestions", "Answer"), ("ClarificationAnswerHistory", "Text") })
            foreach (var record in root[collection]!.AsArray().OfType<JsonObject>())
                if (record[property] is JsonObject reference)
                    record[property] = bodies[reference["$body"]!.GetValue<string>()]!.DeepClone();
        root.Remove("SharedBodies");
        root.Remove("BodyReferenceContract");
        return root;
    }

    internal static Fixture CreateFixture(AgentRole role, bool redundant)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-spec-delivery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "artifact-registry.json"), "{\"artifacts\":[]}");
        var body = redundant ? string.Concat(Enumerable.Repeat("Owner ruling: preserve exact content ✓ including supplementary 🚀. ", 150)) : "compact unique choice";
        var criterion = redundant ? "Exact acceptance criterion: " + new string('c', 400) : "compact criterion";
        var spec = new RefinedSpec("Complete contract", [criterion], VerificationClass.TestVerifiable,
            [new("owner question", body, "source decision")], redundant
                ? [new("question-1", "owner question", "contract", "Answered", body, "scope", "normalized", criterion, "all consumers")]
                : [])
        {
            ClarificationAnswerHistory = redundant ? [new("answer-1", body, DateTimeOffset.UnixEpoch, BriefVersion: 1)] : []
        };
        var objective = redundant ? $"Unique brief requirement\r\n## Acceptance criteria\r\n1. {criterion}\r\n## Operations\r\nPreserve unique operations.\r\n" : "Compact unique objective";
        var task = new TaskSpec(new TaskId("11111111111111111111111111111111"), "Implement scoped fixture", role);
        var goal = new Goal(new GoalId("22222222222222222222222222222222"), objective, [task]);
        if (redundant)
            goal.SetRefinedSpec(spec with { AcceptanceCriteria = ["historical criterion"], BehavioralContract = "superseded contract" }, DateTimeOffset.UnixEpoch.AddDays(-1));
        goal.SetRefinedSpec(spec, DateTimeOffset.UnixEpoch);
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([goal.ToSnapshot()], []));
        goal = kernel.GetGoal(goal.Id);
        task = goal.Tasks.Single();
        var source = kernel.BuildTaskBriefSource(goal.Id, task.Id);
        var package = WorkerProfileDispatcher.BuildContextPackage(goal, task, root, root, source.ToTaskBrief(""), typedSource: source,
            currentCandidateSha: "matched-fixture-candidate", comparisonBaseSha: "matched-fixture-main");
        var prompt = WorkerContextRenderer.Render(source, package, root, WorkerContextDeliveryPolicy.Initial);
        return new(root, kernel, goal, task, source, package, prompt);
    }

    internal sealed record Fixture(string Root, AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task,
        TaskBriefSource Source, WorkerContextPackage Package, string Prompt) : IDisposable
    {
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed record SectionReport(string Identity, string Hash, string Mode, int Utf16, int Utf8, int AuthoritativeBytes, int FileBackedBytes,
        IReadOnlyList<string> Roles, int ContractVersion);
    private sealed record PackageReport(string Name, int Utf16, int Utf8, IReadOnlyList<SectionReport> Sections);
}
