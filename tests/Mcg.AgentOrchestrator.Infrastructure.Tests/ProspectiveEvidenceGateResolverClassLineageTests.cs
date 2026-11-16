using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe integration tests: every repository, kernel and receipt belongs to one fixture.
public sealed class ProspectiveEvidenceGateResolverClassLineageTests
{
    private const string OldSource = "namespace Before; public class Anchor { } public class OldTests { public void Check() { } private void Helper() { } }";
    private const string AnchorSource = "namespace Before; public class Anchor { }";
    private const string NewSource = "namespace After; public class NewTests { public void Check(int value) { } private void Helper() { } public void Extra() { } }";

    [Theory]
    [InlineData("Before.OldTests.Check(value: 1)")]
    [InlineData("OldTests.Check(value: 1)")]
    [InlineData("Before.OldTests")]
    [InlineData("OldTests")]
    public void Extraction_WithScope_AnswersWithCandidateAndLineage(string selector)
    {
        using var fixture = Extracted();
        Assert.Empty(TestClassRenameEvidence.Read(fixture.Root, "main", fixture.Candidate));
        var evidence = new TestClassExtractionEvidence(fixture.Root, "main", fixture.Candidate);
        Assert.Equal(selector.StartsWith("Before.", StringComparison.Ordinal)
            ? selector.Replace("Before.OldTests", "After.NewTests", StringComparison.Ordinal)
            : selector.Replace("OldTests", "NewTests", StringComparison.Ordinal), evidence.Rewrite(selector, [])?.Selector);
        var wait = fixture.Gate.CreateWait([selector]);
        fixture.Attempt([("After.NewTests", "Check(value: 1)", "Passed")]);
        AssertPending(wait, fixture.Resolve(withScope: false), selector);

        var result = fixture.Resolve();

        Assert.Equal([wait.Id], result.ResolvedRequestIds);
        Assert.Empty(result.UnresolvedWaits);
        Assert.True(wait.IsCompleted);
        Assert.Contains($"old=Before.OldTests new=After.NewTests kind=extraction candidate={fixture.Candidate}",
            wait.Answer, StringComparison.Ordinal);
        Assert.Equal(HumanInputAnswerOrigin.Worker, wait.AuthoritativeAnswer!.Origin);
    }

    [Fact]
    public void Rename_WithEndpointIdentity_AnswersWithRenameProvenance()
    {
        var source = OldSource.Replace(" public", "\npublic", StringComparison.Ordinal)
            .Replace(" private", "\nprivate", StringComparison.Ordinal);
        using var fixture = new Repository(("Old.cs", source),
            ("Unrelated.cs", "// \\U00000058\nfile class Unrelated { }"));
        fixture.Remove("Old.cs");
        fixture.CandidateFiles(("New.cs", source.Replace("OldTests", "NewTests", StringComparison.Ordinal)));
        Assert.Single(TestClassRenameEvidence.Read(fixture.Root, "main", fixture.Candidate));
        var wait = fixture.Gate.CreateWait(["Before.OldTests.Check"]);
        fixture.Attempt([("Before.NewTests", "Check", "Passed")]);

        var result = fixture.Resolve();

        Assert.Equal([wait.Id], result.ResolvedRequestIds);
        Assert.Empty(result.UnresolvedWaits);
        Assert.Contains($"old=Before.OldTests new=Before.NewTests kind=rename candidate={fixture.Candidate}",
            wait.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("kind=extraction", wait.Answer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("failed-extracted")]
    [InlineData("wrong-candidate")]
    [InlineData("two-destinations")]
    [InlineData("still-declared")]
    [InlineData("split-methods")]
    [InlineData("helper-missing")]
    [InlineData("direct-failed")]
    [InlineData("direct-ambiguous")]
    public void UnsafeEvidence_StaysPendingWithOriginalSelector(string scenario)
    {
        using var fixture = new Repository(("Old.cs", OldSource));
        var destination = scenario switch
        {
            "split-methods" => "namespace After; public class NewTests { public void Check() { } } public class OtherTests { private void Helper() { } }",
            "helper-missing" => "namespace After; public class NewTests { public void Check() { } }",
            "two-destinations" => NewSource + " public class OtherTests { public void Check() { } private void Helper() { } }",
            _ => NewSource
        };
        fixture.CandidateFiles(("Old.cs", scenario == "still-declared" ? OldSource : AnchorSource), ("New.cs", destination));
        var selector = scenario == "direct-ambiguous" ? "OldTests.Check" : "Before.OldTests.Check";
        var wait = fixture.Gate.CreateWait([selector]);
        var rows = new List<(string, string, string)>
        {
            ("After.NewTests", "Check", scenario == "failed-extracted" ? "Failed" : "Passed")
        };
        if (scenario == "direct-failed") rows.Add(("Before.OldTests", "Check", "Failed"));
        if (scenario == "direct-ambiguous")
        {
            rows.Add(("Before.OldTests", "Check", "Passed"));
            rows.Add(("Unrelated.OldTests", "Check", "Passed"));
        }
        fixture.Attempt(rows.ToArray(), scenario == "wrong-candidate" ? new string('b', 40) : null);

        var result = fixture.Resolve();

        Assert.False(wait.IsCompleted);
        Assert.Null(wait.Answer);
        Assert.Empty(result.ResolvedRequestIds);
        var missing = Assert.Single(Assert.Single(result.UnresolvedWaits).MissingOrFailedTestNames);
        Assert.Equal(scenario == "direct-ambiguous"
            ? selector + " (ambiguous: Before.OldTests, Unrelated.OldTests)" : selector, missing);
    }

    [Theory]
    [InlineData("Tests.Check")]
    [InlineData("New.Check")]
    [InlineData("NeverDeclared.Check")]
    [InlineData("Old.Check")]
    public void CoincidentNames_WithoutHistoricalClassLink_StayPending(string selector)
    {
        using var fixture = Extracted();
        var wait = fixture.Gate.CreateWait([selector]);
        fixture.Attempt([("After.NewTests", "Check", "Passed")]);

        AssertPending(wait, fixture.Resolve(), selector);
    }

    [Fact]
    public void CompleteNameSet_AfterOldClassDisappears_AllowsChangedSignatures()
    {
        using var fixture = Extracted();
        var evidence = new TestClassExtractionEvidence(fixture.Root, "main", fixture.Candidate);
        var old = Assert.Single(evidence.Classes(evidence.MergeBase, "OldTests"));

        var link = evidence.Read(old);

        Assert.NotNull(link);
        Assert.Equal("Before.OldTests", link.OldClass);
        Assert.Equal("After.NewTests", link.NewClass);
        Assert.Equal(fixture.Candidate, link.CandidateSha);
        Assert.Equal(evidence.MergeBase, link.MergeBase);
    }

    [Theory]
    [InlineData("old-namespace")]
    [InlineData("old-assembly")]
    [InlineData("old-nested")]
    [InlineData("new-namespace")]
    [InlineData("new-assembly")]
    public void ShortIdentity_WithConflictingDeclarations_StaysPending(string scenario)
    {
        var baseline = new List<(string, string)> { ("Old.cs", OldSource) };
        if (scenario == "old-namespace") baseline.Add(("Other.cs", "namespace Other; public class OldTests { public void Different() { } }"));
        if (scenario == "old-nested") baseline.Add(("Other.cs", "namespace Other; public class Outer { public class OldTests { public void Different() { } } }"));
        if (scenario == "old-assembly")
        {
            baseline.Add(("One/One.csproj", "<Project />"));
            baseline.Add(("Two/Two.csproj", "<Project />"));
            baseline.Add(("One/Old.cs", "namespace Before; public partial class OldTests { public void Check() { } }"));
            baseline.Add(("Two/Old.cs", "namespace Before; public partial class OldTests { private void Helper() { } }"));
        }
        using var fixture = new Repository(baseline.ToArray());
        var candidate = new List<(string, string)> { ("Old.cs", AnchorSource), ("New.cs", NewSource) };
        if (scenario == "new-namespace") candidate.Add(("Other.cs", "namespace Other; public class NewTests { public void Different() { } }"));
        if (scenario == "new-assembly")
        {
            candidate.Add(("One/One.csproj", "<Project />"));
            candidate.Add(("One/New.cs", NewSource));
        }
        fixture.CandidateFiles(candidate.ToArray());
        var wait = fixture.Gate.CreateWait(["OldTests.Check"]);
        fixture.Attempt([("After.NewTests", "Check", "Passed")]);

        AssertPending(wait, fixture.Resolve(), "OldTests.Check");
    }

    [Fact]
    public void QualifiedIdentity_DisambiguatesHistoricalNamespaces()
    {
        using var fixture = new Repository(("Old.cs", OldSource),
            ("Other.cs", "namespace Other; public class OldTests { public void Different() { } }"));
        fixture.CandidateFiles(("Old.cs", AnchorSource), ("New.cs", NewSource));
        var wait = fixture.Gate.CreateWait(["Before.OldTests.Check"]);
        fixture.Attempt([("After.NewTests", "Check", "Passed")]);

        Assert.Equal([wait.Id], fixture.Resolve().ResolvedRequestIds);
    }

    [Fact]
    public void PartialDeclarations_CombineDirectMethodsAndExcludeNestedMethods()
    {
        using var fixture = new Repository(
            ("Old.cs", "namespace Before; public partial class OldTests { public OldTests() { } public void Check() { void Local() { } } public class Nested { public void Ignore() { } } }"),
            ("Part.cs", "namespace Before; public partial class OldTests { private void Helper() { } public int Property { get; set; } }"));
        fixture.CandidateFiles(("Old.cs", AnchorSource), ("Part.cs", ""),
            ("New.cs", "namespace After; public partial class NewTests { public void Check() { } }"),
            ("NewPart.cs", "namespace After; public partial class NewTests { private void Helper() { } }"));
        var wait = fixture.Gate.CreateWait(["OldTests.Check"]);
        fixture.Attempt([("After.NewTests", "Check", "Passed")]);

        Assert.Equal([wait.Id], fixture.Resolve().ResolvedRequestIds);
        Assert.Contains("kind=extraction", wait.Answer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Before.Outer+OldTests.Check")]
    [InlineData("Outer+OldTests.Check")]
    [InlineData("OldTests.Check")]
    public void NestedIdentity_RewritesInTheOriginalForm(string selector)
    {
        using var fixture = new Repository(("Old.cs", "namespace Before; public class Outer { public class OldTests { public void Check() { } } }"));
        fixture.CandidateFiles(("Old.cs", AnchorSource),
            ("New.cs", "namespace After; public class Container { public class NewTests { public void Check() { } } }"));
        var evidence = new TestClassExtractionEvidence(fixture.Root, "main", fixture.Candidate);
        var rewritten = evidence.Rewrite(selector, []);
        var expected = selector.StartsWith("Before.", StringComparison.Ordinal) ? "After.Container+NewTests.Check"
            : selector.StartsWith("Outer+", StringComparison.Ordinal) ? "Container+NewTests.Check" : "NewTests.Check";
        Assert.Equal(expected, rewritten?.Selector);
        var wait = fixture.Gate.CreateWait([selector]);
        fixture.Attempt([("After.Container+NewTests", "Check", "Passed")]);

        Assert.Equal([wait.Id], fixture.Resolve().ResolvedRequestIds);
    }

    [Theory]
    [InlineData("Before.OldTests.Check(value: 2)")]
    [InlineData("Before.OldTests.Missing")]
    public void Rewrite_PreservesMethodAndCaseConstraints(string selector)
    {
        using var fixture = Extracted();
        var wait = fixture.Gate.CreateWait([selector]);
        fixture.Attempt([("After.NewTests", "Check(value: 1)", "Passed")]);

        AssertPending(wait, fixture.Resolve(), selector);
    }

    [Fact]
    public void SeparateAttempts_CannotPoolRewrittenSelectors()
    {
        using var fixture = Extracted();
        var wait = fixture.Gate.CreateWait(["OldTests.Check", "OldTests.Helper"]);
        fixture.Attempt([("After.NewTests", "Check", "Passed")]);
        fixture.Attempt([("After.NewTests", "Helper", "Passed")]);

        var result = fixture.Resolve();

        Assert.False(wait.IsCompleted);
        Assert.Empty(result.ResolvedRequestIds);
        Assert.Single(Assert.Single(result.UnresolvedWaits).MissingOrFailedTestNames);
    }

    [Fact]
    public void WorkingTreeChanges_DoNotAlterCommittedLineage()
    {
        using var fixture = Extracted();
        fixture.Write("Old.cs", OldSource);
        fixture.Write("New.cs", "invalid C#");
        var wait = fixture.Gate.CreateWait(["OldTests.Check"]);
        fixture.Attempt([("After.NewTests", "Check", "Passed")]);

        Assert.Equal([wait.Id], fixture.Resolve().ResolvedRequestIds);
    }

    [Theory]
    [InlineData("syntax")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("missing-history")]
    public void IndeterminateDeclarations_StayPending(string scenario)
    {
        using var fixture = new Repository(("Old.cs", scenario == "empty"
            ? "namespace Before; public class OldTests { }" : OldSource));
        var source = scenario == "syntax" ? "namespace After; public class NewTests { void Check( }"
            : scenario == "duplicate" ? NewSource + " public class NewTests { public void Check() { } }" : NewSource;
        fixture.CandidateFiles(("Old.cs", AnchorSource), ("New.cs", source));
        var wait = fixture.Gate.CreateWait(["OldTests.Check"]);
        fixture.Attempt([("After.NewTests", "Check", "Passed")]);

        AssertPending(wait, fixture.Resolve(branch: scenario == "missing-history" ? "absent" : "main"), "OldTests.Check");
    }

    [Theory]
    [InlineData("// \\u0058\nfile static class Unrelated { void Other() { } }")]
    [InlineData("// \\U00000058\n#if NEVER\nclass Unrelated { void Other() { } }\n#endif\n")]
    [InlineData("// \\u0058\nclass Unrelated { void Other( }")]
    [InlineData("// \\u0058\n#if NEVER\nclass Unrelated { string Text = \"OldTests Check\"; }\n#endif\n")]
    public void UnrelatedDeclarations_WithEscapeSearchHits_DoNotVetoExtraction(string source)
    {
        using var fixture = new Repository(("Old.cs", OldSource), ("Unrelated.cs", source));
        fixture.CandidateFiles(("Old.cs", AnchorSource), ("New.cs", NewSource));
        var wait = fixture.Gate.CreateWait(["OldTests.Check"]);
        fixture.Attempt([("After.NewTests", "Check", "Passed")]);

        Assert.Equal([wait.Id], fixture.Resolve().ResolvedRequestIds);
        Assert.True(wait.IsCompleted);
        Assert.Contains($"old=Before.OldTests new=After.NewTests kind=extraction candidate={fixture.Candidate}",
            wait.Answer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("// \\u0058\nfile static class Unrelated { void Other() { } }")]
    [InlineData("// \\u0058\n#if NEVER\nclass Unrelated { void Other() { } }\n#endif\n")]
    [InlineData("// \\u0058\nclass Unrelated { void Other( }")]
    public void UnrelatedDeclarations_InTheSameBlob_DoNotVetoExtraction(string source)
    {
        using var fixture = new Repository(("Old.cs", OldSource + "\n" + source));
        fixture.CandidateFiles(("Old.cs", AnchorSource), ("New.cs", NewSource + "\n" + source));
        var wait = fixture.Gate.CreateWait(["OldTests.Check"]);
        fixture.Attempt([("After.NewTests", "Check", "Passed")]);

        Assert.Equal([wait.Id], fixture.Resolve().ResolvedRequestIds);
    }

    [Fact]
    public void EscapedDeclarationIdentifiers_StillEstablishExtraction()
    {
        using var fixture = new Repository(("Old.cs", OldSource
            .Replace("OldTests", "Old\\u0054ests", StringComparison.Ordinal)
            .Replace("Check", "Ch\\u0065ck", StringComparison.Ordinal)));
        fixture.CandidateFiles(("Old.cs", AnchorSource), ("New.cs", NewSource
            .Replace("NewTests", "N\\u0065wTests", StringComparison.Ordinal)
            .Replace("Check", "Ch\\u0065ck", StringComparison.Ordinal)));
        var wait = fixture.Gate.CreateWait(["Before.OldTests.Check"]);
        fixture.Attempt([("After.NewTests", "Check", "Passed")]);

        Assert.Equal([wait.Id], fixture.Resolve().ResolvedRequestIds);
        Assert.Contains($"old=Before.OldTests new=After.NewTests kind=extraction candidate={fixture.Candidate}",
            wait.Answer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("// \\u0058\nfile class NewTests { void Check() { } void Helper() { } }")]
    [InlineData("// \\u0058\n#if NEVER\nclass NewTests { void Check() { } void Helper() { } }\n#endif\n")]
    [InlineData("// \\u0058\nclass NewTests { void Check( }")]
    public void RelevantIndeterminateDeclarations_StayPending(string source)
    {
        using var fixture = new Repository(("Old.cs", OldSource));
        fixture.CandidateFiles(("Old.cs", AnchorSource), ("New.cs", NewSource), ("Uncertain.cs", source));
        var wait = fixture.Gate.CreateWait(["OldTests.Check"]);
        fixture.Attempt([("After.NewTests", "Check", "Passed")]);

        AssertPending(wait, fixture.Resolve(), "OldTests.Check");
    }

    [Theory]
    [InlineData("OldTests.Check<T>")]
    [InlineData("Invalid class.Check")]
    [InlineData("OldTests.")]
    public void InvalidSelector_DoesNotDisableOtherWaits(string selector)
    {
        using var fixture = Extracted();
        var invalid = fixture.Gate.CreateWait([selector]);
        var valid = fixture.Gate.CreateWait(["OldTests.Check"]);
        fixture.Attempt([("After.NewTests", "Check", "Passed")]);

        var result = fixture.Resolve();

        Assert.Equal([valid.Id], result.ResolvedRequestIds);
        Assert.True(valid.IsCompleted);
        Assert.False(invalid.IsCompleted);
        Assert.Equal([selector], Assert.Single(result.UnresolvedWaits).MissingOrFailedTestNames);
    }

    [Fact]
    public void IndeterminateSelector_DoesNotDisableOtherWaits()
    {
        using var fixture = new Repository(("Old.cs", OldSource),
            ("Other.cs", "class UncertainTests { void Run() { } }"));
        fixture.CandidateFiles(("Old.cs", AnchorSource), ("New.cs", NewSource),
            ("Other.cs", "file class UncertainTests { void Run() { } }"));
        var uncertain = fixture.Gate.CreateWait(["UncertainTests.Run"]);
        var valid = fixture.Gate.CreateWait(["OldTests.Check"]);
        fixture.Attempt([("After.NewTests", "Check", "Passed")]);

        var result = fixture.Resolve();

        Assert.Equal([valid.Id], result.ResolvedRequestIds);
        Assert.False(uncertain.IsCompleted);
        Assert.Equal(["UncertainTests.Run"], Assert.Single(result.UnresolvedWaits).MissingOrFailedTestNames);
    }

    [Theory]
    [InlineData("OldTests.Check", false)]
    [InlineData("Before.OldTests.Check", true)]
    public void ShortName_ReintroducedAtCandidate_OnlyQualifiedSelectorCanFollow(string selector, bool resolves)
    {
        using var fixture = new Repository(("Old.cs", OldSource));
        fixture.CandidateFiles(("Old.cs", AnchorSource), ("New.cs", NewSource),
            ("Unrelated.cs", "namespace Unrelated; class OldTests { void Other() { } }"));
        var wait = fixture.Gate.CreateWait([selector]);
        fixture.Attempt([("After.NewTests", "Check", "Passed")]);

        var result = fixture.Resolve();

        if (resolves) Assert.Equal([wait.Id], result.ResolvedRequestIds);
        else AssertPending(wait, result, selector);
    }

    private static Repository Extracted()
    {
        var fixture = new Repository(("Old.cs", OldSource));
        fixture.CandidateFiles(("Old.cs", AnchorSource), ("New.cs", NewSource));
        return fixture;
    }

    private static void AssertPending(HumanInputRequest wait, ProspectiveEvidenceGateResolution result, string selector)
    {
        Assert.False(wait.IsCompleted);
        Assert.Null(wait.Answer);
        Assert.Empty(result.ResolvedRequestIds);
        var unresolved = Assert.Single(result.UnresolvedWaits);
        Assert.Equal(wait.Id, unresolved.RequestId);
        Assert.Equal([selector], unresolved.MissingOrFailedTestNames);
    }

    private sealed class Repository : IDisposable
    {
        internal ProspectiveEvidenceGateFixture Gate { get; } = new();
        internal string Root { get; }
        internal string Candidate { get; private set; } = "";

        internal Repository(params (string Path, string Source)[] baseline)
        {
            Root = Path.Combine(Gate.Root, "repository");
            Directory.CreateDirectory(Root);
            Git("init", "-b", "main");
            Git("config", "user.name", "Class Lineage Test");
            Git("config", "user.email", "lineage@example.invalid");
            Git("config", "core.hooksPath", Path.Combine(Root, "no-hooks"));
            foreach (var (path, source) in baseline) Write(path, source);
            Commit();
            Git("checkout", "-b", "goal");
        }

        internal void Write(string path, string source)
        {
            var full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, source);
        }

        internal void Remove(string path) => File.Delete(Path.Combine(Root, path));

        internal void CandidateFiles(params (string Path, string Source)[] files)
        {
            foreach (var (path, source) in files) Write(path, source);
            Commit();
            Candidate = Git("rev-parse", "HEAD").Trim();
        }

        internal void Attempt((string Class, string Method, string Outcome)[] rows, string? candidate = null)
        {
            var (_, trx, _) = ProspectiveEvidenceGateFixture.WriteAttempt(Gate.OrchestratorDirectory,
                Gate.Goal, candidate ?? Candidate, rows.Select(row => (row.Class + "." + row.Method, row.Outcome)));
            new XDocument(new XElement("TestRun",
                new XElement("TestDefinitions", rows.Select((row, index) => new XElement("UnitTest",
                    new XAttribute("id", index), new XElement("TestMethod", new XAttribute("className", row.Class),
                        new XAttribute("name", row.Method.Split('(')[0]))))),
                new XElement("Results", rows.Select((row, index) => new XElement("UnitTestResult",
                    new XAttribute("testId", index), new XAttribute("testName", row.Class + "." + row.Method),
                    new XAttribute("outcome", row.Outcome)))))).Save(trx);
        }

        internal ProspectiveEvidenceGateResolution Resolve(bool withScope = true, string branch = "main") =>
            ProspectiveEvidenceGateResolver.Resolve(Gate.Kernel, Gate.Goal, Gate.OrchestratorDirectory, Candidate,
                withScope ? new(Root, branch) : null);

        private void Commit()
        {
            Git("add", "--all");
            Git("-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "Class lineage fixture");
        }

        private string Git(params string[] args)
        {
            var result = GitCli.Run(Root, args);
            Assert.True(result.Succeeded && !result.DrainTimedOut, $"git {string.Join(' ', args)}: {result.Error}");
            return result.Output;
        }

        public void Dispose()
        {
            // Git object files can be read-only on Windows; this root belongs only to this fixture.
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Gate.Dispose();
        }
    }
}
