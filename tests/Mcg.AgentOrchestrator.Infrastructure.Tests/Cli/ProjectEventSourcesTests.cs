using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Xunit;

// Parallel-safe: each test owns its filesystem root, streams and registry; no global writes.
public sealed class ProjectEventSourcesTests
{
    [Fact]
    public void Dispatch_FleetWithMissingLogs_ExportsExactUtf8WithoutMutation()
    {
        using var fixture = new Fixture();
        var zulu = fixture.Register("zulu");
        var alpha = fixture.Register("alpha");
        var beta = fixture.Register("beta");
        Assert.Equal(alpha.RootDirectory, beta.RootDirectory);
        Assert.NotEqual(alpha.SqliteStatePath, beta.SqliteStatePath);
        fixture.SeedOpaqueState(alpha);
        File.WriteAllText(fixture.Registry.CurrentProjectPath, "unregistered-selection");
        var before = fixture.Snapshot();

        var first = fixture.Dispatch("alpha");
        var second = fixture.Dispatch("does-not-exist");

        Assert.Equal(0, first.Exit);
        Assert.Empty(first.Error);
        Assert.Equal(first.Exit, second.Exit);
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(first.Error, second.Error);
        var expected = $"alpha={alpha.ConductEventsLogPath}\nbeta={beta.ConductEventsLogPath}\n" +
            $"home={fixture.Home.ConductEventsLogPath}\nzulu={zulu.ConductEventsLogPath}\n";
        Assert.Equal(new UTF8Encoding(false, true).GetBytes(expected), first.Bytes);
        Assert.False(first.Bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        var records = Parse(first.Bytes);
        Assert.Equal(new[] { "alpha", "beta", "home", "zulu" }, records.Select(record => record.Label));
        Assert.All(records, record => Assert.True(Path.IsPathFullyQualified(record.Path)));
        Assert.Contains("données = fleet", records[0].Path);
        Assert.All(records, record => Assert.False(File.Exists(record.Path)));
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Catalog_RelocatedHomeAlias_ExportsHomeOnce(bool tenantScoped)
    {
        using var fixture = new Fixture();
        var alpha = fixture.Register("alpha", tenantScoped ? "team" : null);
        var beta = fixture.Register("beta", tenantScoped ? "team" : null);
        var stateRoot = tenantScoped ? Path.GetDirectoryName(Path.GetDirectoryName(alpha.OrchestratorDirectory))!
            : alpha.OrchestratorDirectory;
        fixture.Registry.RecordDefaultStateLocation(new(fixture.Repository,
            Path.GetRelativePath(fixture.Data, stateRoot), ".orchestrator.backup-fixture", DateTimeOffset.UnixEpoch));
        var tenant = tenantScoped ? "team" : null;
        var home = OrchestratorWorkspace.ForDirectory(fixture.Repository, null, tenant, fixture.Registry);
        Assert.Equal(alpha.SqliteStatePath, home.SqliteStatePath);
        var before = fixture.Snapshot();

        var result = fixture.Dispatch("beta", tenant: tenant);

        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Error);
        Assert.Equal($"beta={beta.ConductEventsLogPath}\nhome={home.ConductEventsLogPath}\n", result.Text);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void Dispatch_ExplicitHomeBinding_UsesSuppliedPaths()
    {
        using var fixture = new Fixture();
        var home = fixture.Home with { LogDirectory = Path.Combine(fixture.Root, "explicit home = logs") };
        var before = fixture.Snapshot();

        var result = fixture.Dispatch("ignored", home: home);

        Assert.Equal(0, result.Exit);
        Assert.Equal($"home={home.ConductEventsLogPath}\n", result.Text);
        Assert.Empty(result.Error);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dispatch_StateExpectation_VerifiesHomeInsteadOfSelection(bool mismatch)
    {
        using var fixture = new Fixture();
        var selected = fixture.Register("alpha");
        var home = fixture.Home;
        var binding = new WorkspaceInvocationBinding(fixture.Repository,
            mismatch ? selected.OrchestratorDirectory : home.OrchestratorDirectory, false, []);
        var before = fixture.Snapshot();
        var result = fixture.Dispatch("alpha", binding: binding);
        if (mismatch)
        {
            AssertFailure(result);
            Assert.Contains("differs from expected", result.Error);
        }
        else
        {
            Assert.Equal(0, result.Exit);
            Assert.Empty(result.Error);
            Assert.Equal(new[] { "alpha", "home" }, Parse(result.Bytes).Select(record => record.Label));
        }
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Serialization_ReversedAliases_UsesStableCanonicalLabels(bool reverse)
    {
        using var fixture = new Fixture();
        var shared = fixture.Register("alpha");
        var other = fixture.Register("other");
        AttentionWorkspaceEntry[] entries = [new("zulu", shared), new("alpha", shared),
            new("Alpha", shared), new("Zed", other), new("home", fixture.Home)];
        if (reverse) Array.Reverse(entries);
        var calls = 0;

        var result = fixture.Export(() => { calls++; return new(entries); });

        Assert.Equal(1, calls);
        Assert.Equal(0, result.Exit);
        Assert.Equal($"Alpha={shared.ConductEventsLogPath}\nZed={other.ConductEventsLogPath}\n" +
            $"home={fixture.Home.ConductEventsLogPath}\n", result.Text);
        Assert.Equal(new[] { "Alpha", "Zed", "home" }, Parse(result.Bytes).Select(record => record.Label));
    }

    [Theory]
    [InlineData("corrupt-registry")]
    [InlineData("home-conflict")]
    [InlineData("invalid-binding")]
    public void Dispatch_RegistryOrBindingFailure_EmitsOnlyStderr(string failure)
    {
        using var fixture = new Fixture();
        fixture.Register("alpha");
        fixture.SeedOpaqueState(fixture.Home);
        File.WriteAllText(fixture.Registry.CurrentProjectPath, "alpha");
        if (failure == "corrupt-registry") File.WriteAllText(fixture.Registry.RegistryPath, "{bad json");
        else if (failure == "home-conflict") fixture.Register("home");
        else
        {
            // The owning resolver rejects escaping relative state mappings; do not bypass it.
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture.Registry.RegistryPath))!;
            json["DefaultProjectStateLocations"] = System.Text.Json.JsonSerializer.SerializeToNode(new[]
            {
                new DefaultProjectStateLocation(fixture.Repository, "../escape", ".orchestrator.backup-fixture", DateTimeOffset.UnixEpoch)
            });
            File.WriteAllText(fixture.Registry.RegistryPath, json.ToJsonString());
        }
        var before = fixture.Snapshot();

        var result = fixture.Dispatch(null);

        AssertFailure(result);
        Assert.Contains(failure switch
        {
            "corrupt-registry" => "invalid start",
            "home-conflict" => "conflicts with the fleet home identity",
            _ => "Default state location must stay under the data root"
        }, result.Error);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("registry")]
    [InlineData("entry")]
    [InlineData("unresolved")]
    [InlineData("throw")]
    public void Catalog_LateResolutionFailure_DiscardsHealthyRecords(string failure)
    {
        using var fixture = new Fixture();
        var before = fixture.Snapshot();
        var exception = new IOException("resolution marker\n" + new string('x', 9000));
        var calls = 0;

        var result = fixture.Export(() =>
        {
            calls++;
            if (failure == "throw") throw exception;
            AttentionWorkspaceEntry[] entries = [new("alpha", fixture.Home),
                new("zulu", failure == "unresolved" ? null : fixture.Home,
                    failure == "entry" ? exception : null)];
            return new(entries, failure == "registry" ? exception : null);
        });

        Assert.Equal(1, calls);
        AssertFailure(result);
        if (failure != "unresolved")
        {
            Assert.Contains("resolution marker\\n", result.Error);
            Assert.Equal(4096, result.Error.Length);
        }
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad=label")]
    [InlineData("bad\rlabel")]
    [InlineData("bad\nlabel")]
    [InlineData("bad\vlabel")]
    [InlineData("bad\flabel")]
    [InlineData("bad\u0085label")]
    [InlineData("bad\u2028label")]
    [InlineData("bad\u2029label")]
    public void Serialization_UnrepresentableLabel_RejectsWholeList(string label)
    {
        using var fixture = new Fixture();
        var other = fixture.Register("other");
        var result = fixture.Export(() => new([new("alpha", fixture.Home), new(label, other)]));
        AssertFailure(result);
        Assert.Contains("label cannot be represented", result.Error);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\v")]
    [InlineData("\f")]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\0")]
    public void Serialization_UnresolvedOrInvalidLogPath_RejectsWholeList(string invalid)
    {
        using var fixture = new Fixture();
        var other = fixture.Register("other");
        var bad = other with { LogDirectory = invalid is "relative" or "" ? invalid : other.LogDirectory + invalid };
        var before = fixture.Snapshot();
        var result = fixture.Export(() => new([new("alpha", fixture.Home), new("zulu", bad)]));
        AssertFailure(result);
        Assert.Contains("representable absolute path", result.Error);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void Serialization_DuplicateCanonicalLabels_RejectsWholeList()
    {
        using var fixture = new Fixture();
        var other = fixture.Register("other");
        var result = fixture.Export(() => new([new("same", fixture.Home), new("same", other)]));
        AssertFailure(result);
        Assert.Contains("label cannot be represented", result.Error);
    }

    [Fact]
    public void Serialization_RelativeStoreIdentity_RejectsAmbientFallback()
    {
        using var fixture = new Fixture();
        var bad = fixture.Home with { SqliteStatePath = "relative-state.db" };
        var result = fixture.Export(() => new([new("home", bad)]));
        AssertFailure(result);
        Assert.Contains("representable absolute path", result.Error);
    }

    [Fact]
    public void Serialization_LiteralQuotesAndEquals_PreservesTextWithoutEscaping()
    {
        using var fixture = new Fixture();
        var home = fixture.Home with { LogDirectory = Path.Combine(fixture.Root, "literal \"quotes\" = logs") };
        var result = fixture.Export(() => new([new("literal \"étiquette\"", home)]));
        Assert.Equal(0, result.Exit);
        var record = Assert.Single(Parse(result.Bytes));
        Assert.Equal("literal \"étiquette\"", record.Label);
        Assert.Equal(home.ConductEventsLogPath, record.Path);
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("--out=file")]
    [InlineData("--json")]
    public void Dispatch_ExtraArguments_FailsWithoutSourceRecords(string argument)
    {
        using var fixture = new Fixture();
        var before = fixture.Snapshot();
        var result = fixture.Dispatch(null, ["project", "event-sources", argument]);
        AssertFailure(result);
        Assert.Contains(ProjectEventSourcesCommand.Usage, result.Error);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void Help_EventSourcesFlags_RejectsOutputModes()
    {
        CliCommandHelp.ThrowIfInvalidFlags(["project", "event-sources", "--help"]);
        Assert.Throws<ArgumentException>(() =>
            CliCommandHelp.ThrowIfInvalidFlags(["project", "event-sources", "--json"]));
    }

    private static void AssertFailure(Result result)
    {
        Assert.Equal(1, result.Exit);
        Assert.Empty(result.Bytes);
        Assert.StartsWith("project event-sources: ", result.Error);
        Assert.InRange(result.Error.Length, 1, 4096);
        Assert.EndsWith("\n", result.Error);
        Assert.DoesNotContain('\n', result.Error[..^1]);
        Assert.DoesNotContain('\r', result.Error);
    }

    private static (string Label, string Path)[] Parse(byte[] bytes)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes);
        Assert.EndsWith("\n", text);
        Assert.DoesNotContain('\r', text);
        var records = text[..^1].Split('\n').Select(line =>
        {
            var equals = line.IndexOf('=');
            Assert.True(equals > 0);
            return (Label: line[..equals], Path: line[(equals + 1)..]);
        }).ToArray();
        Assert.All(records, record => Assert.False(string.IsNullOrWhiteSpace(record.Label)));
        Assert.Equal(records.Length, records.Select(record => record.Label).Distinct(StringComparer.Ordinal).Count());
        return records;
    }

    private sealed record Result(int Exit, byte[] Bytes, string Error)
    {
        internal string Text => new UTF8Encoding(false, true).GetString(Bytes);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("event-sources-").FullName;
        internal string Repository => Path.Combine(Root, "repository space = home");
        internal string Data => Path.Combine(Root, "données = fleet");
        internal OrchestratorProjectRegistry Registry { get; }
        internal OrchestratorWorkspace Home => OrchestratorWorkspace.ForDirectory(Repository, null, null, Registry);
        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Repository, ".git"));
            Registry = new(Path.Combine(Root, "registry"), Data);
        }
        internal OrchestratorWorkspace Register(string name, string? tenant = null)
        {
            Registry.CreateProject(name, Repository);
            return OrchestratorWorkspace.ForProject(name, Repository, tenantName: tenant, dataRootDirectory: Data);
        }
        internal void SeedOpaqueState(OrchestratorWorkspace workspace)
        {
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            File.WriteAllText(workspace.SqliteStatePath, "opaque state: must not be opened or migrated");
            Directory.CreateDirectory(workspace.LogDirectory);
            File.WriteAllText(Path.Combine(workspace.LogDirectory, "existing.log"), "preserve log");
        }
        internal Result Dispatch(string? selected, string[]? parts = null, string? tenant = null,
            OrchestratorWorkspace? home = null, WorkspaceInvocationBinding? binding = null)
        {
            using var output = new MemoryStream();
            using var error = new StringWriter();
            var exit = ProjectCliCommand.Execute(parts ?? ["project", "event-sources"], Registry, Repository, selected,
                eventSourcesOutput: output, eventSourcesError: error, tenant: tenant, homeWorkspace: home,
                eventSourcesBinding: binding);
            return new(exit, output.ToArray(), error.ToString());
        }
        internal Result Export(Func<AttentionWorkspaceCatalog> catalog)
        {
            using var output = new MemoryStream();
            using var error = new StringWriter();
            var exit = ProjectEventSourcesCommand.Execute(["project", "event-sources"], Registry, Repository,
                output, error, readCatalog: catalog);
            return new(exit, output.ToArray(), error.ToString());
        }
        internal string[] Snapshot() => Directory.GetFileSystemEntries(Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Directory.Exists(path)
                ? "directory:" + Path.GetRelativePath(Root, path)
                : "file:" + Path.GetRelativePath(Root, path) + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" +
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
