public sealed class EnvironmentVariableCensusTests
{
    // Parallel-safe: synthetic controls use only local strings and no filesystem or process state.
    [Xunit.Fact]
    public void FindUnlisted_MissingName_ReportsNameDespiteOtherMentions()
    {
        const string source = """const string Variable = "MCG_SYNTHETIC_NAME";""";
        const string table = """
            MCG_SYNTHETIC_NAME is mentioned here, but has no census row.
            | Name | Reading path:line | Category | Default | Setter | Target home |
            | --- | --- | --- | --- | --- | --- |
            | `MCG_OTHER_NAME` | synthetic.cs:1 | setting | MCG_SYNTHETIC_NAME | tests | config |
            """;

        var missing = EnvironmentVariableCensusScanner.FindUnlisted([source], table);

        Xunit.Assert.Equal(new[] { "MCG_SYNTHETIC_NAME" }, missing);
    }

    [Xunit.Fact]
    public void FindUnlisted_ListedName_ReportsNothing()
    {
        const string source = """const string Variable = "MCG_SYNTHETIC_NAME";""";
        const string table = """
            | Name | Reading path:line | Category | Default | Setter | Target home |
            | --- | --- | --- | --- | --- | --- |
            | `MCG_SYNTHETIC_NAME` | synthetic.cs:1 | setting | absent | tests | config |
            """;

        var missing = EnvironmentVariableCensusScanner.FindUnlisted([source], table);

        Xunit.Assert.Empty(missing);
    }

    [Xunit.Fact]
    public void FindUnlisted_RepeatedMissingNames_ReturnsDistinctOrdinalNames()
    {
        string[] sources =
        [
            """const string Z = "MCG_Z_MISSING"; const string A = "MCG_A_MISSING";""",
            """const string Duplicate = "MCG_Z_MISSING";"""
        ];

        var missing = EnvironmentVariableCensusScanner.FindUnlisted(sources, string.Empty);

        Xunit.Assert.Equal(new[] { "MCG_A_MISSING", "MCG_Z_MISSING" }, missing);
    }

    [Xunit.Theory]
    [Xunit.InlineData("setting", "setting", "identity")]
    [Xunit.InlineData("setting (capacity)", "setting", "identity")]
    [Xunit.InlineData("identity", "identity", "setting")]
    [Xunit.InlineData("identity (project)", "identity", "setting")]
    public void FindUnallowlisted_SettingAndIdentityRows_RequireAllowlistUnderOwnCategory(
        string category, string ownGroup, string otherGroup)
    {
        string[][] rows = [["MCG_SYNTHETIC_NAME", "synthetic.cs:1", category, "absent", "tests", "home"]];
        var allowlist = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["setting"] = new HashSet<string>(StringComparer.Ordinal),
            ["identity"] = new HashSet<string>(StringComparer.Ordinal)
        };
        Xunit.Assert.Single(EnvironmentVariableCensusScanner.FindUnallowlisted(rows, allowlist));

        allowlist[otherGroup] = new HashSet<string>(["MCG_SYNTHETIC_NAME"], StringComparer.Ordinal);
        Xunit.Assert.Single(EnvironmentVariableCensusScanner.FindUnallowlisted(rows, allowlist));

        allowlist[otherGroup] = new HashSet<string>(StringComparer.Ordinal);
        allowlist[ownGroup] = new HashSet<string>(["MCG_SYNTHETIC_NAME", "MCG_UNUSED_NAME"], StringComparer.Ordinal);
        Xunit.Assert.Empty(EnvironmentVariableCensusScanner.FindUnallowlisted(rows, allowlist));

        string[][] unaffected =
        [
            ["MCG_HANDOFF", "synthetic.cs:1", "handoff", "absent", "tests", "handoff"],
            ["MCG_TEST_ONLY", "synthetic.cs:2", "test-only", "absent", "tests", "test seam"]
        ];
        Xunit.Assert.Empty(EnvironmentVariableCensusScanner.FindUnallowlisted(unaffected, allowlist));
    }

    [Xunit.Theory]
    [Xunit.InlineData("setting (capacity)", ".orchestrator/conductor-policy.json")]
    [Xunit.InlineData("identity (project)", "an explicit argument at the entry point")]
    public void FindUnallowlisted_UnlistedRow_NamesVariableCategoryAndHome(string category, string home)
    {
        string[][] rows = [["MCG_SYNTHETIC_NAME", "synthetic.cs:1", category, "absent", "tests", "obsolete home"]];
        var violation = Xunit.Assert.Single(EnvironmentVariableCensusScanner.FindUnallowlisted(rows,
            new Dictionary<string, IReadOnlySet<string>>()));

        Xunit.Assert.Contains("MCG_SYNTHETIC_NAME", violation);
        Xunit.Assert.Contains(category, violation);
        Xunit.Assert.Contains(home, violation);
        Xunit.Assert.Contains("AGENTS.md, Architecture & Design Discipline", violation);
        Xunit.Assert.Contains("Choose where a value lives by what kind of value it is.", violation);
    }

    [Xunit.Theory]
    [Xunit.InlineData("not JSON")]
    [Xunit.InlineData("[]")]
    [Xunit.InlineData("{}")]
    [Xunit.InlineData("{\"setting\":[],\"identity\":[],\"other\":[]}")]
    [Xunit.InlineData("{\"setting\":[],\"setting\":[],\"identity\":[]}")]
    [Xunit.InlineData("{\"setting\":null,\"identity\":[]}")]
    [Xunit.InlineData("{\"setting\":[1],\"identity\":[]}")]
    [Xunit.InlineData("{\"setting\":[\"OTHER_NAME\"],\"identity\":[]}")]
    [Xunit.InlineData("{\"setting\":[\"MCG_Z\",\"MCG_A\"],\"identity\":[]}")]
    [Xunit.InlineData("{\"setting\":[\"MCG_A\",\"MCG_A\"],\"identity\":[]}")]
    [Xunit.InlineData("{\"setting\":[\"MCG_A\"],\"identity\":[\"MCG_A\"]}")]
    public void ReadAllowlist_InvalidDocument_NamesAllowlistFile(string json)
    {
        var exception = Xunit.Assert.Throws<FormatException>(() => EnvironmentVariableCensusScanner.ReadAllowlist(json));
        Xunit.Assert.Contains("config/environment-variable-allowlist.json", exception.Message);
    }

    // Parallel-safe: reads the explicit candidate root without modifying files or process state.
    [Xunit.Fact]
    public void RepositoryCensus_SettingAndIdentityRows_AreAllowlistedAndLegacyLaunchIsAbsent()
    {
        var root = VerifiedRepositoryRoot.Find();
        var table = File.ReadAllText(Path.Combine(root, "docs", "environment-variables.md"));
        var rows = EnvironmentVariableCensusScanner.ReadRows(table);
        Xunit.Assert.NotEmpty(rows);
        var path = Path.Combine(root, "config", "environment-variable-allowlist.json");
        Xunit.Assert.True(File.Exists(path), $"Missing environment variable allowlist: {path}");
        var allowlist = EnvironmentVariableCensusScanner.ReadAllowlist(File.ReadAllText(path), path);
        var violations = EnvironmentVariableCensusScanner.FindUnallowlisted(rows, allowlist);
        Xunit.Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));

        const string removedName = "MCG_GIT_LEGACY_LAUNCH";
        Xunit.Assert.DoesNotContain(rows, row => row[0] == removedName);
        Xunit.Assert.All(allowlist.Values, names => Xunit.Assert.DoesNotContain(removedName, names));
        var files = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories);
        Xunit.Assert.NotEmpty(files);
        Xunit.Assert.All(files, file => Xunit.Assert.False(File.ReadAllText(file).Contains(removedName,
            StringComparison.Ordinal), $"{removedName} remains in {file}"));
    }

    // Parallel-safe: reads the explicit candidate root without modifying files or process state.
    [Xunit.Fact]
    public void RepositoryCensus_MovedFeatures_AreAbsentFromSourcesCensusAndAllowlist()
    {
        var root = VerifiedRepositoryRoot.Find();
        var table = File.ReadAllText(Path.Combine(root, "docs", "environment-variables.md"));
        var allowlist = File.ReadAllText(Path.Combine(root, "config", "environment-variable-allowlist.json"));
        var files = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories);
        Xunit.Assert.NotEmpty(files);
        string[] removed =
        [
            "MCG_ORCHESTRATOR_STEWARD_ENABLED", "MCG_ORCHESTRATOR_AUTHOR_ENABLED",
            "MCG_ORCHESTRATOR_SELF_RELAUNCH_ENABLED", "MCG_MERGE_TRAIN_DISABLED",
            "MCG_ARCHIVED_SNAPSHOT_COMPRESSION"
        ];
        foreach (var name in removed)
        {
            Xunit.Assert.DoesNotContain(name, table, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain(name, allowlist, StringComparison.Ordinal);
        }
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (var name in removed)
                Xunit.Assert.False(source.Contains(name, StringComparison.Ordinal), $"{name} remains in {file}");
        }
    }

    // Parallel-safe: reads the explicit candidate root without modifying files or process state.
    [Xunit.Fact]
    public void RepositorySources_AllNames_HaveCompleteUniqueCensusRows()
    {
        var root = VerifiedRepositoryRoot.Find();
        var files = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories);
        Xunit.Assert.NotEmpty(files);
        var sources = files.Select(File.ReadAllText).ToArray();
        var names = EnvironmentVariableCensusScanner.DiscoverNames(sources);
        Xunit.Assert.Contains("MCG_WORKER_ACCOUNT", names);

        var table = File.ReadAllText(Path.Combine(root, "docs", "environment-variables.md"));
        var missing = EnvironmentVariableCensusScanner.FindUnlisted(sources, table);
        Xunit.Assert.True(missing.Count == 0, "Unlisted source names: " + string.Join(", ", missing));

        var rows = EnvironmentVariableCensusScanner.ReadRows(table);
        Xunit.Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            Xunit.Assert.True(row.Length == 6, $"{row[0]} must have six cells; found {row.Length}.");
            Xunit.Assert.All(row, cell => Xunit.Assert.False(string.IsNullOrWhiteSpace(cell)));
        }

        var listed = rows.Select(row => row[0]).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        // Sequence equality rejects duplicates and extra rows as well as omissions.
        Xunit.Assert.Equal(names.ToArray(), listed);
    }

    [Xunit.Fact]
    public void SandboxConstant_Discovery_IncludesNameWithoutLiteralAtReadCall()
    {
        var root = VerifiedRepositoryRoot.Find();
        var source = File.ReadAllText(Path.Combine(root,
            "src", "Mcg.AgentOrchestrator.Execution", "Processes", "WorkerSandboxOptions.cs"));

        Xunit.Assert.Contains("GetEnvironmentVariable(AccountVariable)", source);
        Xunit.Assert.Contains("MCG_WORKER_ACCOUNT",
            EnvironmentVariableCensusScanner.DiscoverNames([source]));
    }
}
