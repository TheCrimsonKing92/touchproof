using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

[LiveTreeStructural]
public sealed class AgentHarnessDocsDriftTests
{
    private const string AgentsPath = "AGENTS.md";
    private const string ClaudePath = "CLAUDE.md";
    private const string RepositoryConventionsPath = "docs/repository-conventions.md";
    private const string TestDesignDisciplinePath = "docs/test-design-discipline.md";
    private const string FixtureProcedure = "docs/negative-controls/nested/fixture.md";
    private const string FixtureScript = "scripts/nested/Invoke-FixtureProcedure.ps1";

    // Frozen at introduction. Remove an entry when it gains coverage or becomes stale.
    private static readonly OperatorProcedureCoverage.Reference[] UncoveredProcedureBaseline = [];

    private static readonly SharedSection[] SharedSections =
    [
        new("output-discipline", "## Output Discipline"),
        new("retry-and-loop-control", "## Retry and Loop Control"),
        new("repository-rules", "## Repository Rules"),
        new("architecture-and-design-discipline", "## Architecture & Design Discipline"),
        new("specification-discipline", "## Specification Discipline"),
        new("diagnosis-discipline", "## Diagnosis Discipline"),
        new("dogfood-boundary", "## Dogfood Boundary"),
        new("operating-the-goal-loop", "## Operating the goal loop"),
        new("safety", "## Safety"),
        new("evidence", "## Evidence")
    ];

    // Credits the SharedHomeSections approach proposed by the worker on goal c4d02669.
    private static readonly SharedHomeSection[] SharedHomeSections =
    [
        new(
            TestDesignDisciplinePath,
            "<!-- shared-discipline:test-design-discipline -->",
            "another shared home",
            [AgentsPath, ClaudePath]),
        new(
            RepositoryConventionsPath,
            "<!-- shared-discipline:repository-conventions -->",
            "another shared home",
            [AgentsPath, ClaudePath])
    ];

    [Xunit.Fact(DisplayName = "Harness_docs_counterpart_contracts_stay_in_sync")]
    public void HarnessDocsCounterpartContractsStayInSync()
    {
        var root = FindRepositoryRoot();
        var docs = ReadHarnessDocs(root);

        Validate(root, docs);
    }

    [Xunit.Fact(DisplayName = "Dogfood_boundary_anchor_rename_keeps_script_and_test_shared_sections_in_order")]
    public void DogfoodBoundaryAnchorRenameKeepsScriptAndTestSharedSectionsInOrder()
    {
        var root = FindRepositoryRoot();
        var docs = ReadHarnessDocs(root);
        foreach (var text in new[] { docs.Agents, docs.Claude })
        {
            Xunit.Assert.DoesNotContain("dashboard-dogfood-boundary", text);
            Xunit.Assert.DoesNotContain("## Dashboard / Dogfood Boundary", text.Split('\n').Select(line => line.TrimEnd('\r')));
        }

        Xunit.Assert.Contains("<!-- shared-discipline:dogfood-boundary -->", docs.Agents);
        Xunit.Assert.Contains("## Dogfood Boundary", docs.Agents.Split('\n').Select(line => line.TrimEnd('\r')));
        var agentsAnchors = ParseContract(AgentsPath, docs.Agents).SharedAnchors;
        var claudeAnchors = ParseContract(ClaudePath, docs.Claude).SharedAnchors;
        Xunit.Assert.Equal(agentsAnchors, claudeAnchors);
        Xunit.Assert.Contains("dogfood-boundary", agentsAnchors);

        var script = File.ReadAllText(Path.Combine(root, "scripts/Test-AgentHarnessDocsDrift.ps1"));
        var block = Regex.Match(script, @"(?m)^\$sharedSections = @\(\r?\n(?<entries>[\s\S]*?)^\)\r?$");
        Xunit.Assert.True(block.Success, "$sharedSections block not found.");
        var scriptSections = Regex.Matches(
                block.Groups["entries"].Value,
                @"@\{\s*Anchor\s*=\s*'(?<anchor>[^']+)';\s*Heading\s*=\s*'(?<heading>[^']+)'\s*\}")
            .Select(match => new SharedSection(match.Groups["anchor"].Value, match.Groups["heading"].Value))
            .ToArray();
        Xunit.Assert.NotEmpty(scriptSections);
        Xunit.Assert.Equal(SharedSections, scriptSections);
    }

    [Xunit.Fact(DisplayName = "Harness_docs_drift_check_rejects_unpaired_contract_or_anchor_edits")]
    public void HarnessDocsDriftCheckRejectsUnpairedContractOrAnchorEdits()
    {
        var root = FindRepositoryRoot();
        var docs = ReadHarnessDocs(root);

        var contractDrift = docs.Claude.Replace(
            "Owns: Claude Code harness operating guidance.",
            "Owns: Edited Claude harness guidance.",
            StringComparison.Ordinal);
        var contractException = Xunit.Assert.ThrowsAny<InvalidOperationException>(
            () => Validate(root, docs with { Claude = contractDrift }));
        Xunit.Assert.True(contractException.Message.Contains("CLAUDE.md contract Owns", StringComparison.Ordinal));

        var anchorDrift = docs.Claude.Replace(
            "- evidence",
            "- evidence\n- invented-shared-discipline",
            StringComparison.Ordinal);
        var anchorException = Xunit.Assert.ThrowsAny<InvalidOperationException>(
            () => Validate(root, docs with { Claude = anchorDrift }));
        Xunit.Assert.True(anchorException.Message.Contains("Shared anchor lists must match", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Shared_home_drift_check_rejects_a_missing_document")]
    public void SharedHomeDriftCheckRejectsAMissingDocument()
    {
        using var fixture = SharedHomeFixture.Create();
        var section = GetTestDesignDisciplineSection();
        File.Delete(fixture.GetPath(section.DocPath));

        var exception = Xunit.Assert.ThrowsAny<InvalidOperationException>(
            () => Validate(fixture.Root, fixture.Docs));

        Xunit.Assert.True(exception.Message.Contains(section.DocPath, StringComparison.Ordinal));
        Xunit.Assert.True(exception.Message.Contains("does not exist", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Shared_home_drift_check_rejects_a_missing_marker")]
    public void SharedHomeDriftCheckRejectsAMissingMarker()
    {
        using var fixture = SharedHomeFixture.Create();
        var section = GetTestDesignDisciplineSection();
        var documentPath = fixture.GetPath(section.DocPath);
        var document = File.ReadAllText(documentPath);
        File.WriteAllText(
            documentPath,
            document.Replace(section.SharedDisciplineMarker, string.Empty, StringComparison.Ordinal));

        var exception = Xunit.Assert.ThrowsAny<InvalidOperationException>(
            () => Validate(fixture.Root, fixture.Docs));

        Xunit.Assert.True(exception.Message.Contains(section.DocPath, StringComparison.Ordinal));
        Xunit.Assert.True(exception.Message.Contains(section.SharedDisciplineMarker, StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Shared_home_drift_check_rejects_a_missing_harness_link")]
    public void SharedHomeDriftCheckRejectsAMissingHarnessLink()
    {
        using var fixture = SharedHomeFixture.Create();
        var section = GetTestDesignDisciplineSection();
        var claudeWithoutLink = fixture.Docs.Claude.Replace(
            $"]({section.DocPath})",
            $"](docs/missing-shared-home.md)\n\nProse only: {section.DocPath}",
            StringComparison.Ordinal);
        Xunit.Assert.NotEqual(fixture.Docs.Claude, claudeWithoutLink);

        var exception = Xunit.Assert.ThrowsAny<InvalidOperationException>(
            () => Validate(fixture.Root, fixture.Docs with { Claude = claudeWithoutLink }));

        Xunit.Assert.True(exception.Message.Contains(ClaudePath, StringComparison.Ordinal));
        Xunit.Assert.True(exception.Message.Contains(section.DocPath, StringComparison.Ordinal));
        Xunit.Assert.True(exception.Message.Contains("Markdown link target", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Shared_home_drift_check_rejects_a_missing_escape_hatch")]
    public void SharedHomeDriftCheckRejectsAMissingEscapeHatch()
    {
        using var fixture = SharedHomeFixture.Create();
        var section = GetTestDesignDisciplineSection();
        var claudeWithoutEscapeHatch = fixture.Docs.Claude.Replace(
            section.EscapeHatchName,
            "a separately reviewed shared location",
            StringComparison.Ordinal);
        Xunit.Assert.NotEqual(fixture.Docs.Claude, claudeWithoutEscapeHatch);

        var exception = Xunit.Assert.ThrowsAny<InvalidOperationException>(
            () => Validate(fixture.Root, fixture.Docs with { Claude = claudeWithoutEscapeHatch }));

        Xunit.Assert.True(exception.Message.Contains(ClaudePath, StringComparison.Ordinal));
        Xunit.Assert.True(exception.Message.Contains(section.EscapeHatchName, StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void ProcedureScriptWithoutTestLiteralIsFlagged()
    {
        var files = ProcedureFixture();
        files["docs/negative-controls/other.md"] = "`wrong-directory/Invoke-FixtureProcedure.ps1`";
        files["docs/other.md"] = "`Invoke-FixtureProcedure.ps1`";
        files["src/Sample.cs"] = "var script = \"Invoke-FixtureProcedure.ps1\";";

        Xunit.Assert.Equal(new(FixtureProcedure, FixtureScript),
            Xunit.Assert.Single(OperatorProcedureCoverage.FindUncovered(files)));
    }

    [Xunit.Theory]
    [Xunit.InlineData("var script = \"Invoke-FixtureProcedure.ps1\";")]
    [Xunit.InlineData("var script = \"scripts\\\\nested\\\\Invoke-FixtureProcedure.ps1\";")]
    [Xunit.InlineData("var script = @\".\\scripts\\nested\\Invoke-FixtureProcedure.ps1\";")]
    [Xunit.InlineData("var script = \"\"\"https://localhost scripts/nested/Invoke-FixtureProcedure.ps1\"\"\";")]
    [Xunit.InlineData("var script = $\"{root}/scripts/nested/Invoke-FixtureProcedure.ps1\";")]
    [Xunit.InlineData("var script = $$\"\"\"{{root}}/scripts/nested/Invoke-FixtureProcedure.ps1\"\"\";")]
    [Xunit.InlineData("var script = $\"{GetPath(\"Invoke-FixtureProcedure.ps1\")}\";")]
    [Xunit.InlineData("var script = \"Invoke-FixtureProcedure\\u002eps1\";")]
    public void ProcedureScriptInTestStringLiteralPasses(string testSource)
    {
        var files = ProcedureFixture();
        files["tests/nested/Sample.cs"] = testSource;

        Xunit.Assert.Empty(OperatorProcedureCoverage.FindUncovered(files));
    }

    [Xunit.Theory]
    [Xunit.InlineData("var other = \"other\"; // \"Invoke-FixtureProcedure.ps1\"")]
    [Xunit.InlineData("/* \"scripts/nested/Invoke-FixtureProcedure.ps1\" */")]
    [Xunit.InlineData("var script = $\"{value /* \"Invoke-FixtureProcedure.ps1\" */}\";")]
    [Xunit.InlineData("var script = \"Invoke-Fixture\" + \"Procedure.ps1\";")]
    [Xunit.InlineData("var script = $\"Invoke-Fixture{part}.ps1\";")]
    [Xunit.InlineData("var script = \"Other-Invoke-FixtureProcedure.ps1\";")]
    public void ProcedureScriptWithoutACompleteLiteralIsFlagged(string testSource)
    {
        var files = ProcedureFixture();
        files["tests/nested/Sample.cs"] = testSource;

        Xunit.Assert.Equal(new(FixtureProcedure, FixtureScript),
            Xunit.Assert.Single(OperatorProcedureCoverage.FindUncovered(files)));
    }

    [Xunit.Fact]
    public void BareScriptNameFlagsEveryMatchingScriptOnce()
    {
        var files = ProcedureFixture();
        files[FixtureProcedure] = "Run `Invoke-FixtureProcedure.ps1` or `./Invoke-FixtureProcedure.ps1`.";
        const string secondScript = "scripts/other/Invoke-FixtureProcedure.ps1";
        files[secondScript] = "";

        Xunit.Assert.Equal(new OperatorProcedureCoverage.Reference[]
        {
            new(FixtureProcedure, FixtureScript),
            new(FixtureProcedure, secondScript)
        }, OperatorProcedureCoverage.FindUncovered(files));
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void BaselineComparisonRejectsNewAndCoveredOrStaleEntries(bool coverOriginal)
    {
        var files = ProcedureFixture();
        var baseline = OperatorProcedureCoverage.FindUncovered(files);
        if (coverOriginal)
            files["tests/Sample.cs"] = "var script = \"Invoke-FixtureProcedure.ps1\";";
        else
            files.Remove(FixtureProcedure);

        const string newProcedure = "docs/negative-controls/new.md";
        const string newScript = "scripts/fixture-control";
        files[newProcedure] = "Run `scripts/fixture-control`.";
        files[newScript] = "";

        var (added, stale) = OperatorProcedureCoverage.CompareBaseline(
            OperatorProcedureCoverage.FindUncovered(files), baseline);

        Xunit.Assert.Equal(new(newProcedure, newScript), Xunit.Assert.Single(added));
        Xunit.Assert.Equal(new(FixtureProcedure, FixtureScript), Xunit.Assert.Single(stale));
    }

    [Xunit.Fact]
    public void RepositoryProceduresMatchFrozenUncoveredBaseline()
    {
        var root = FindRepositoryRoot();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        AddTree("docs/negative-controls", "*.md", readContents: true);
        AddTree("scripts", "*", readContents: false);
        AddTree("tests", "*.cs", readContents: true);

        var completionBuild = new OperatorProcedureCoverage.Reference(
            "docs/negative-controls/b176116e-completion-build-operator.md",
            "scripts/Invoke-CompletionBuildOperatorControl.ps1");
        Xunit.Assert.DoesNotContain(completionBuild, UncoveredProcedureBaseline);

        // The guard and its baseline cannot supply script references for the procedures they check.
        files.Remove("tests/Mcg.AgentOrchestrator.Core.Tests/AgentHarnessDocsDriftTests.cs");
        files.Remove("tests/Mcg.AgentOrchestrator.Core.Tests/OperatorProcedureCoverage.cs");
        var current = OperatorProcedureCoverage.FindUncovered(files);
        Xunit.Assert.DoesNotContain(completionBuild, current);
        var (added, stale) = OperatorProcedureCoverage.CompareBaseline(current, UncoveredProcedureBaseline);
        Xunit.Assert.True(added.Length == 0 && stale.Length == 0,
            $"New uncovered procedures: [{string.Join(", ", added.AsEnumerable())}]. " +
            $"Covered or stale baseline entries: [{string.Join(", ", stale.AsEnumerable())}].");

        void AddTree(string directory, string pattern, bool readContents)
        {
            var countBefore = files.Count;
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, directory), pattern, SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!relative.Split('/').Any(segment => segment is "bin" or "obj" or ".scratch"
                        or ".orchestrator-prototype" or "TestResults" or "playwright-report"))
                {
                    files.Add(relative, readContents ? File.ReadAllText(path) : "");
                }
            }
            Xunit.Assert.True(files.Count > countBefore, $"No procedure coverage inputs found in {directory}.");
        }
    }

    private static Dictionary<string, string> ProcedureFixture() => new()
    {
        [FixtureProcedure] = "Run `.\\scripts\\nested\\Invoke-FixtureProcedure.ps1`.",
        [FixtureScript] = ""
    };

    private static void Validate(string root, HarnessDocs docs)
    {
        var agents = docs.Agents;
        var claude = docs.Claude;
        RequireContains(agents, "docs/operator-runbook.md", "AGENTS.md must reference docs/operator-runbook.md.");
        RequireContains(claude, "docs/operator-runbook.md", "CLAUDE.md must reference docs/operator-runbook.md.");

        var agentsContract = ParseContract(AgentsPath, agents);
        var claudeContract = ParseContract(ClaudePath, claude);

        foreach (var section in SharedHomeSections)
        {
            var documentPath = Path.Combine(root, section.DocPath);
            if (!File.Exists(documentPath))
            {
                throw new InvalidOperationException($"Shared home {section.DocPath} does not exist.");
            }

            var document = File.ReadAllText(documentPath);
            RequireContains(
                document,
                section.SharedDisciplineMarker,
                $"{section.DocPath} must carry shared-discipline marker {section.SharedDisciplineMarker}.");

            foreach (var harnessPath in section.ReferencedFrom)
            {
                var harnessText = GetHarnessText(docs, harnessPath);
                if (!ContainsMarkdownLinkTarget(harnessText, section.DocPath))
                {
                    throw new InvalidOperationException(
                        $"{harnessPath} must reference {section.DocPath} as a Markdown link target.");
                }

                var harnessContract = GetHarnessContract(agentsContract, claudeContract, harnessPath);
                RequireContains(
                    harnessContract.Rule,
                    section.EscapeHatchName,
                    $"{harnessPath} counterpart contract must contain escape-hatch identifier '{section.EscapeHatchName}' for {section.DocPath}.");
            }
        }

        RequireEqual(
            "shared repository discipline plus Codex harness operating guidance.",
            agentsContract.Owns,
            "AGENTS.md contract Owns changed.");
        RequireEqual(
            "CLAUDE.md owns Claude Code harness operating guidance and links back to AGENTS.md for shared discipline.",
            agentsContract.Counterpart,
            "AGENTS.md contract Counterpart changed.");
        RequireEqual(
            "Claude Code harness operating guidance.",
            claudeContract.Owns,
            "CLAUDE.md contract Owns changed.");
        RequireEqual(
            "AGENTS.md owns shared repository discipline plus Codex harness operating guidance.",
            claudeContract.Counterpart,
            "CLAUDE.md contract Counterpart changed.");
        RequireEqual(agentsContract.Rule, claudeContract.Rule, "Counterpart contract rules must match.");
        RequireContains(agentsContract.Rule, "update BOTH AGENTS.md and CLAUDE.md", "Contract rule must require updating both files.");
        RequireContains(agentsContract.Rule, "move the content to docs/operator-runbook.md or another shared home", "Contract rule must name the shared-home escape hatch.");

        var expectedAnchors = SharedSections.Select(section => section.Anchor).ToArray();
        RequireSequenceEqual(expectedAnchors, agentsContract.SharedAnchors, "AGENTS.md shared-anchor list changed.");
        RequireSequenceEqual(agentsContract.SharedAnchors, claudeContract.SharedAnchors, "Shared anchor lists must match.");

        foreach (var section in SharedSections)
        {
            var marker = $"<!-- shared-discipline:{section.Anchor} -->";
            RequireContains(agents, marker, $"AGENTS.md must carry shared marker {marker}.");
            RequireContains(agents, section.Heading, $"AGENTS.md must carry shared heading {section.Heading}.");
            RequireDoesNotContain(claude, marker, $"CLAUDE.md must not duplicate shared marker {marker}.");
            RequireDoesNotContain(claude, section.Heading, $"CLAUDE.md must not duplicate shared heading {section.Heading}.");
        }
    }

    private static SharedHomeSection GetTestDesignDisciplineSection() =>
        SharedHomeSections.Single(section => section.DocPath.Equals(TestDesignDisciplinePath, StringComparison.Ordinal));

    private static string GetHarnessText(HarnessDocs docs, string path) => path switch
    {
        AgentsPath => docs.Agents,
        ClaudePath => docs.Claude,
        _ => throw new InvalidOperationException($"Shared home registry names unknown harness file {path}.")
    };

    private static HarnessContract GetHarnessContract(
        HarnessContract agentsContract,
        HarnessContract claudeContract,
        string path) => path switch
    {
        AgentsPath => agentsContract,
        ClaudePath => claudeContract,
        _ => throw new InvalidOperationException($"Shared home registry names unknown counterpart contract {path}.")
    };

    private static bool ContainsMarkdownLinkTarget(string markdown, string expectedTarget)
    {
        var matches = Regex.Matches(markdown, @"\]\(\s*(?<target><[^>\r\n]+>|[^\s)\r\n]+)\s*\)");
        return matches
            .Select(match => match.Groups["target"].Value.Trim('<', '>'))
            .Any(target => target.Equals(expectedTarget, StringComparison.Ordinal));
    }

    private static HarnessDocs ReadHarnessDocs(string root)
    {
        return new HarnessDocs(
            File.ReadAllText(Path.Combine(root, AgentsPath)),
            File.ReadAllText(Path.Combine(root, ClaudePath)));
    }

    private static HarnessContract ParseContract(string path, string text)
    {
        var matches = Regex.Matches(
            text,
            @"<!-- HARNESS-COUNTERPART-CONTRACT:BEGIN -->(?<body>.*?)<!-- HARNESS-COUNTERPART-CONTRACT:END -->",
            RegexOptions.Singleline);
        if (matches.Count != 1)
        {
            throw new InvalidOperationException($"{path} must contain exactly one counterpart contract block; found {matches.Count}.");
        }

        var body = matches[0].Groups["body"].Value.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = body.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var owns = ParseField(path, lines, "Owns:");
        var counterpart = ParseField(path, lines, "Counterpart:");
        var rule = ParseField(path, lines, "Rule:");
        var sharedAnchorsIndex = Array.FindIndex(lines, line => line.Equals("Shared anchors:", StringComparison.Ordinal));
        if (sharedAnchorsIndex < 0)
        {
            throw new InvalidOperationException($"{path} counterpart contract must include Shared anchors.");
        }

        var anchors = lines
            .Skip(sharedAnchorsIndex + 1)
            .TakeWhile(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line[2..])
            .ToArray();
        if (anchors.Length == 0)
        {
            throw new InvalidOperationException($"{path} counterpart contract must list shared anchors.");
        }

        return new HarnessContract(owns, counterpart, rule, anchors);
    }

    private static string ParseField(string path, IReadOnlyList<string> lines, string field)
    {
        var line = lines.SingleOrDefault(line => line.StartsWith(field, StringComparison.Ordinal));
        if (line is null)
        {
            throw new InvalidOperationException($"{path} counterpart contract must include {field}");
        }

        return line[field.Length..].Trim();
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) &&
                File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repository root from source file path '{sourceFilePath}'.");
    }

    private static void RequireContains(string value, string expected, string message)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireDoesNotContain(string value, string unexpected, string message)
    {
        if (value.Contains(unexpected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireEqual(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
        }
    }

    private static void RequireSequenceEqual(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string message)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"{message} Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        }
    }

    private sealed record SharedSection(string Anchor, string Heading);

    private sealed record SharedHomeSection(
        string DocPath,
        string SharedDisciplineMarker,
        string EscapeHatchName,
        IReadOnlyList<string> ReferencedFrom);

    private sealed record HarnessContract(
        string Owns,
        string Counterpart,
        string Rule,
        IReadOnlyList<string> SharedAnchors);

    private sealed record HarnessDocs(string Agents, string Claude);

    private sealed class SharedHomeFixture : IDisposable
    {
        private SharedHomeFixture(string root, HarnessDocs docs)
        {
            Root = root;
            Docs = docs;
        }

        public string Root { get; }

        public HarnessDocs Docs { get; }

        public static SharedHomeFixture Create()
        {
            var repositoryRoot = FindRepositoryRoot();
            var fixtureRoot = Path.Combine(
                Path.GetTempPath(),
                nameof(AgentHarnessDocsDriftTests),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureRoot);

            foreach (var section in SharedHomeSections)
            {
                var fixturePath = Path.Combine(fixtureRoot, section.DocPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
                File.Copy(Path.Combine(repositoryRoot, section.DocPath), fixturePath);
            }

            return new SharedHomeFixture(fixtureRoot, ReadHarnessDocs(repositoryRoot));
        }

        public string GetPath(string repositoryRelativePath) => Path.Combine(Root, repositoryRelativePath);

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
