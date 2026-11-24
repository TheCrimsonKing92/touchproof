using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.App.Orchestration.PreTesterReverseDependencyObservation;
using static PreTesterResolverObservationTests;

public sealed class PreTesterResolverCorpusTests
{
    [Fact]
    public void ReconstructsInventoryAndRunsBothIdentifiedArmsInRecordedOrder()
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        var path = MakeCorpus(fixture.Root);
        var seal = Receipt(path);
        File.Delete(Path.Combine(fixture.Root, PreTesterReverseDependencySelectionFixture.Project));
        var candidate = Build(fixture.Root, PreTesterResolverCorpus.CandidateRevision);
        var oracle = Build(fixture.Root, PreTesterResolverCorpus.OracleRevision);
        var receipt = Path.Combine(fixture.Root, "replay.json");

        var corpus = PreTesterResolverCorpus.Read(path, seal.Sha256!);
        Assert.Equal(["A", "B", "A"], corpus.Cases[0].Inventory.Select(descriptor => descriptor.FullName));
        Assert.Equal(["A", "B", "A"], corpus.Cases[1].Invocation.Names);
        var candidatePath = WriteBuild(candidate, fixture.Root, "candidate");
        var oraclePath = WriteBuild(oracle, fixture.Root, "oracle");
        using var error = new StringWriter();
        var exitCode = PreTesterResolverReplayCommand.Run(
            [PreTesterResolverReplayCommand.Name, path, seal.Sha256!, receipt, candidatePath, oraclePath], error);
        Assert.True(exitCode == 0, error.ToString());
        Assert.Equal("", error.ToString());

        var result = JsonSerializer.Deserialize<PreTesterResolverCorpus.ReplayReceipt>(File.ReadAllBytes(receipt))!;
        Assert.Equal(seal.Sha256, result.CorpusSha256);
        Assert.Equal(seal.WindowId, result.WindowId);
        var producer = Assert.Single(result.Producers);
        Assert.Equal(seal.SessionId, producer.SessionId);
        Assert.NotEqual(producer.AppSha256, result.Candidate.AppSha256);
        Assert.NotEqual(result.Candidate.AppSha256, result.Oracle.AppSha256);
        Assert.Equal([1, 2, 3, 4], result.Comparisons.Select(comparison => comparison.Ordinal));
        Assert.All(result.Comparisons, comparison => Assert.True(comparison.Equivalent));
        Assert.All(result.Comparisons.Take(2), comparison =>
            Assert.Contains("class=A kind=reverse-dependency-unresolved", Assert.Single(comparison.Candidate.Diagnostics)));
        Assert.Equal("PRE_TESTER_GUARD_SELECTED class=none kind=reverse-dependency-skipped-cost fixture-cost",
            Assert.Single(result.Comparisons[2].Oracle.Diagnostics));
        Assert.Equal(["B", "A"], result.Comparisons[3].Candidate.Selections.Select(selection => selection.TestClass));
        Assert.Equal(["src/Shipping.cs", "src/Other.cs", "src/Shipping.cs", "src/Other.cs"],
            result.Comparisons[3].Oracle.Provenance.Select(item => item.Path));
        Assert.Equal(seal.Sha256, Sha256(path));
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("ordinal-with-new-digest")]
    [InlineData("candidate")]
    [InlineData("oracle-revision")]
    [InlineData("embedded-revision")]
    [InlineData("unverified-ancestry")]
    public void FailsBeforeEitherArmOnDigestOrdinalOrBuildMismatch(string mismatch)
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        var path = MakeCorpus(fixture.Root);
        var seal = Receipt(path);
        var candidate = Build(fixture.Root, PreTesterResolverCorpus.CandidateRevision);
        var oracle = Build(fixture.Root, PreTesterResolverCorpus.OracleRevision);
        var receipt = Path.Combine(fixture.Root, "replay.json");
        switch (mismatch)
        {
            case "inventory":
                File.WriteAllText(path, File.ReadAllText(path).Replace("\"Inventory\":[\"A\"", "\"Inventory\":[\"X\""));
                break;
            case "ordinal-with-new-digest":
                File.WriteAllText(path, File.ReadAllText(path).Replace("\"Ordinal\":1", "\"Ordinal\":2"));
                seal = seal with { Sha256 = Sha256(path) };
                File.WriteAllBytes(path + ".receipt.json", JsonSerializer.SerializeToUtf8Bytes(seal));
                break;
            case "candidate":
                File.AppendAllText(candidate.AppPath, "tampered");
                break;
            case "oracle-revision":
                File.WriteAllText(oracle.AppPath + ".git-head", candidate.Revision);
                break;
            case "embedded-revision":
                File.Copy(oracle.AppPath, candidate.AppPath, overwrite: true);
                // Keep the files' digests distinct while supplying an App with the wrong embedded revision.
                File.AppendAllText(candidate.AppPath, "overlay");
                candidate = candidate with { AppSha256 = Sha256(candidate.AppPath) };
                break;
            case "unverified-ancestry":
                const string unrelated = "1111111111111111111111111111111111111111";
                File.WriteAllText(candidate.AppPath + ".git-head", unrelated);
                candidate = candidate with { Revision = unrelated, Amendments = ["claimed descendant"] };
                break;
        }

        Assert.Throws<InvalidDataException>(() => PreTesterResolverCorpus.Replay(path, seal.Sha256!, receipt,
            candidate, oracle));
        Assert.False(File.Exists(receipt));
    }

    [Fact]
    public void DifferentCandidateDecisionProducesDigestBoundMismatch()
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        var corpus = MakeCorpus(fixture.Root);
        var digest = Receipt(corpus).Sha256!;
        var candidate = Build(fixture.Root, PreTesterResolverCorpus.CandidateRevision, differentOutcome: true);
        var oracle = Build(fixture.Root, PreTesterResolverCorpus.OracleRevision);
        var receipt = Path.Combine(fixture.Root, "replay.json");

        Assert.False(PreTesterResolverCorpus.Replay(corpus, digest, receipt, candidate, oracle));

        var result = JsonSerializer.Deserialize<PreTesterResolverCorpus.ReplayReceipt>(File.ReadAllBytes(receipt))!;
        Assert.Equal(digest, result.CorpusSha256);
        Assert.Equal("A", result.Comparisons[0].Oracle.UnresolvedName);
        Assert.Null(result.Comparisons[0].Candidate.UnresolvedName);
        Assert.False(result.Comparisons[0].Equivalent);
    }

    [Fact]
    public void AVerifiedDigestForANonExecutableCannotProduceADecisionReceipt()
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        var path = MakeCorpus(fixture.Root);
        var candidate = Build(fixture.Root, PreTesterResolverCorpus.CandidateRevision);
        var oracle = Build(fixture.Root, PreTesterResolverCorpus.OracleRevision);
        File.WriteAllText(candidate.AppPath, "this is not an executable");

        var receipt = Path.Combine(fixture.Root, "replay.json");
        Assert.Throws<BadImageFormatException>(() => PreTesterResolverCorpus.Replay(path, Receipt(path).Sha256!,
            receipt, candidate with { AppSha256 = Sha256(candidate.AppPath) }, oracle));
        Assert.False(File.Exists(receipt));
    }

    private static string MakeCorpus(string root)
    {
        var now = Start;
        var id = Guid.NewGuid();
        using var window = Window(root);
        using var tick = Assert.IsType<Tick>(window.BeginTick(id, () => now));
        var project = PreTesterReverseDependencySelectionFixture.Project;
        for (var index = 0; index < 2; index++)
            tick.Record(tick.Enter()!.Value, BeforeTester, project, ["A", "B", "A"],
                [new("A", null), new("B", null), new("A", null)], ["src/Shipping.cs"], "A", null);
        tick.Record(tick.Enter()!.Value, BeforeTester, project, ["B", "A"],
            [new("A", null), new("B", null)], ["src/Shipping.cs"], null, new(false, "fixture-cost"));
        tick.Record(tick.Enter()!.Value, BeforeTester, project, ["B", "A", "B"],
            [new("A", null), new("B", null)], ["src/Shipping.cs", "src/Other.cs"], null, new(true, "fixture-cost"));
        tick.Complete();
        now += Duration;
        Assert.Null(window.BeginTick(id, () => now));
        return CorpusPath(root, id);
    }

    private static PreTesterResolverCorpus.ReplayBuild Build(string root, string revision, bool differentOutcome = false)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, revision));
        const string appName = "Mcg.AgentOrchestrator.App";
        var app = Path.Combine(directory.FullName, appName + ".dll");
        var core = typeof(FindingEvidenceSelection).Assembly.Location;
        File.Copy(core, Path.Combine(directory.FullName, Path.GetFileName(core)));
        var matcher = revision == PreTesterResolverCorpus.CandidateRevision ? OptimizedMatcher : OriginalMatcher;
        if (differentOutcome) matcher = matcher.Replace("counts[name] != 1", "counts[name] == 0", StringComparison.Ordinal);
        var source = $$"""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using Mcg.AgentOrchestrator.Core;
            [assembly: System.Reflection.AssemblyInformationalVersion("1.0.0+{{revision}}")]
            namespace Mcg.AgentOrchestrator.App.Orchestration;
            internal static class PreTesterReverseDependencySelections
            {
                internal const string Project = "{{PreTesterReverseDependencySelectionFixture.Project}}";
                internal sealed record Descriptor(string FullName, string Collection, string Constraint);
                internal sealed record Cost(bool Run, string Line);
                internal sealed record Provenance(string Class, string Kind, string Path);

                {{matcher}}

                internal static (IReadOnlyList<FindingEvidenceSelection>, IReadOnlyList<Provenance>) CompleteSelection(
                    IReadOnlyList<string> names, string unresolved, IReadOnlyList<string> paths, Action<string> progress,
                    Func<IReadOnlyList<string>, Cost> costDecision, out Cost evaluatedCost)
                {
                    evaluatedCost = null;
                    if (unresolved != null)
                    {
                        progress($"PRE_TESTER_GUARD_SELECTED class={unresolved} kind=reverse-dependency-unresolved reason=class-match-not-unique path={paths[0]}");
                        return ([], []);
                    }

                    var classes = names.Distinct(StringComparer.Ordinal).ToArray();
                    evaluatedCost = costDecision(classes);
                    if (!evaluatedCost.Run)
                    {
                        progress($"PRE_TESTER_GUARD_SELECTED class=none kind=reverse-dependency-skipped-cost {evaluatedCost.Line}");
                        return ([], []);
                    }

                    var selections = classes.Select(name => new FindingEvidenceSelection("Infrastructure.Tests", name)).ToArray();
                    return (selections, selections.SelectMany(selection => paths.Select(path =>
                        new Provenance(selection.TestClass, "reverse-dependency", path))).ToArray());
                }
            }
            """;
        File.WriteAllBytes(app, DependencyIndexFixtureAssembly.Compile(source, appName));
        File.WriteAllText(app + ".git-head", revision);
        return new(revision, revision, app, Sha256(app), []);
    }

    // The two hermetic fixture builds exercise the matching loops from the pinned resolver sources.
    // Production replay uses the full staged Apps. These fixtures verify the adapter and its failure paths.
    private const string OriginalMatcher = """
        internal static string FirstUnresolvedClass(IReadOnlyList<string> names, IEnumerable<Descriptor> inventory)
        {
            foreach (var name in names)
                if (inventory.Count(item => item.FullName == name ||
                        item.FullName.EndsWith("." + name, StringComparison.Ordinal)) != 1)
                    return name;
            return null;
        }
        """;

    private const string OptimizedMatcher = """
        internal static string FirstUnresolvedClass(IReadOnlyList<string> names, IEnumerable<Descriptor> inventory,
            List<string> observedInventory)
        {
            if (names.Count == 0) return null;
            if (names.Count == 1)
            {
                var name = names[0];
                var suffix = "." + name;
                var matches = 0;
                foreach (var item in inventory)
                {
                    observedInventory?.Add(item.FullName);
                    if (item.FullName != name && !item.FullName.EndsWith(suffix, StringComparison.Ordinal)) continue;
                    if (++matches == 2 && observedInventory == null) return name;
                }
                return matches == 1 ? null : name;
            }

            var counts = new Dictionary<string, byte>(names.Count, StringComparer.Ordinal);
            foreach (var name in names)
                counts.TryAdd(name, 0);

            var lookup = counts.GetAlternateLookup<ReadOnlySpan<char>>();
            foreach (var item in inventory)
            {
                observedInventory?.Add(item.FullName);
                var suffix = item.FullName.AsSpan();
                while (true)
                {
                    if (lookup.TryGetValue(suffix, out var count) && count < 2)
                        lookup[suffix] = (byte)(count + 1);
                    var dot = suffix.IndexOf('.');
                    if (dot < 0) break;
                    suffix = suffix[(dot + 1)..];
                }
            }

            foreach (var name in names)
                if (counts[name] != 1)
                    return name;
            return null;
        }
        """;

    private static string WriteBuild(PreTesterResolverCorpus.ReplayBuild build, string root, string arm)
    {
        var path = Path.Combine(root, arm + "-build.json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(build));
        return path;
    }
}
