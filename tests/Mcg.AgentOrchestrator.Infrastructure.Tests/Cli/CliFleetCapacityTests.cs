using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: unique stores/registry; environment is pinned only on isolated child processes.
public sealed class CliFleetCapacityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T12:00:00Z", CultureInfo.InvariantCulture);
    [Fact]
    public void Executable_JsonEnumeratesFleet_WithoutWritableStartupOrHydration()
    {
        using var fixture = new Fixture();
        var alpha = fixture.Register("alpha");
        var beta = fixture.Register("beta");
        fixture.Seed(fixture.Home, "{\"Tasks\":[{\"LastProcess\":{}}]}", 2);
        fixture.Seed(alpha, "{\"Tasks\":[{\"LastProcess\":{}},{\"LastProcess\":{}}]}", 7);
        fixture.Seed(beta, "{\"Tasks\":[]}", 3);
        // These intentionally incomplete Goal bodies cannot hydrate a kernel. Startup must also
        // bypass active selection and all writable setup (which would add stores/config files).
        File.WriteAllText(fixture.Registry.CurrentProjectPath, "unregistered-selection");
        var before = fixture.Snapshot();
        var child = Run(fixture, "fleet-capacity", "--json");
        Assert.Equal(0, child.ExitCode);
        using var document = JsonDocument.Parse(child.StandardOutput);
        var envelope = document.RootElement;
        AssertEnvelope(envelope);
        var payload = envelope.GetProperty("payload");
        Assert.Equal(new[] { "catalogComplete", "complete", "errors", "knownPersistedRunningWorkers", "observedAt",
            "projects", "theoreticalConfiguredProjectWorkerCapSum", "unavailableProjectCount" },
            payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.True(payload.GetProperty("catalogComplete").GetBoolean());
        Assert.True(payload.GetProperty("complete").GetBoolean());
        Assert.Equal(3, payload.GetProperty("knownPersistedRunningWorkers").GetInt64());
        Assert.Equal(12, payload.GetProperty("theoreticalConfiguredProjectWorkerCapSum").GetInt64());
        Assert.Equal(0, payload.GetProperty("unavailableProjectCount").GetInt32());
        Assert.Empty(payload.GetProperty("errors").EnumerateArray());
        var rows = payload.GetProperty("projects").EnumerateArray().ToArray();
        Assert.Equal(new[] { "alpha", "beta", "home" }, rows.Select(row => row.GetProperty("project").GetString()));
        foreach (var row in rows)
        {
            Assert.Equal(new[] { "aliases", "availability", "configuredProjectWorkerCap", "errors", "persistedRunningWorkers", "project" },
                row.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal("available", row.GetProperty("availability").GetString());
            Assert.Empty(row.GetProperty("errors").EnumerateArray());
        }
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(File.Exists(Path.Combine(fixture.Home.OrchestratorDirectory, "collaboration-items.db")));
    }

    [Fact]
    public void Executable_RelocatedHomeAlias_UsesAuthoritativeStateBindingAndCountsOnce()
    {
        using var fixture = new Fixture();
        var alpha = fixture.Register("alpha");
        fixture.Registry.RecordDefaultStateLocation(new(fixture.Repository,
            Path.GetRelativePath(fixture.Data, alpha.OrchestratorDirectory), ".orchestrator.backup-fixture", Now));
        fixture.Seed(alpha, "{\"Tasks\":[{\"LastProcess\":{}}]}", 8);
        var before = fixture.Snapshot();
        var child = Run(fixture, "fleet-capacity", "--json");
        Assert.Equal(0, child.ExitCode);
        using var document = JsonDocument.Parse(child.StandardOutput);
        var payload = document.RootElement.GetProperty("payload");
        var row = Assert.Single(payload.GetProperty("projects").EnumerateArray());
        Assert.Equal("home", row.GetProperty("project").GetString());
        Assert.Equal("alpha", Assert.Single(row.GetProperty("aliases").EnumerateArray()).GetString());
        Assert.Equal(1, payload.GetProperty("knownPersistedRunningWorkers").GetInt64());
        Assert.Equal(8, payload.GetProperty("theoreticalConfiguredProjectWorkerCapSum").GetInt64());
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(Directory.Exists(Path.Combine(fixture.Repository, ".orchestrator")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Executable_PartialData_PreservesRowsAndMetricErrors_ReturnsOne(bool json)
    {
        using var fixture = new Fixture();
        var alpha = fixture.Register("alpha");
        fixture.Seed(fixture.Home, "{\"Tasks\":[{\"LastProcess\":{}}]}", 2);
        fixture.Seed(alpha, "{\"Tasks\":[{\"LastProcess\":{}},{\"LastProcess\":{}}]}", 3);
        File.WriteAllText(Path.Combine(alpha.OrchestratorDirectory, "conductor-policy.json"), "invalid");
        fixture.Register("missing");
        var before = fixture.Snapshot();
        var child = json ? Run(fixture, "fleet-capacity", "--json") : Run(fixture, "fleet-capacity");
        Assert.Equal(1, child.ExitCode);
        if (json)
        {
            using var document = JsonDocument.Parse(child.StandardOutput);
            AssertEnvelope(document.RootElement);
            var payload = document.RootElement.GetProperty("payload");
            Assert.False(payload.GetProperty("complete").GetBoolean());
            Assert.True(payload.GetProperty("catalogComplete").GetBoolean());
            Assert.Equal(2, payload.GetProperty("unavailableProjectCount").GetInt32());
            Assert.Equal(3, payload.GetProperty("knownPersistedRunningWorkers").GetInt64());
            var rows = payload.GetProperty("projects").EnumerateArray().ToArray();
            Assert.Equal(new[] { "alpha", "home", "missing" }, rows.Select(row => row.GetProperty("project").GetString()));
            Assert.Equal(2, rows[0].GetProperty("persistedRunningWorkers").GetInt64());
            Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("configuredProjectWorkerCap").ValueKind);
            var error = Assert.Single(rows[0].GetProperty("errors").EnumerateArray());
            Assert.Equal("invalid-policy", error.GetProperty("code").GetString());
            Assert.Equal("policy", error.GetProperty("metric").GetString());
            Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("persistedRunningWorkers").ValueKind);
            Assert.Equal("missing-store", Assert.Single(rows[2].GetProperty("errors").EnumerateArray()).GetProperty("code").GetString());
        }
        else
        {
            Assert.Contains("alpha: unavailable; recorded workers=2, configured local cap=unknown", child.StandardOutput);
            Assert.Contains("home: available; recorded workers=1", child.StandardOutput);
            Assert.Contains("missing: unavailable; recorded workers=unknown", child.StandardOutput);
            Assert.Contains("Known partial totals: recorded workers=3", child.StandardOutput);
            AssertLimitations(child.StandardOutput);
        }
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void Executable_CompleteTextShowsRecordedAndLocalLimitSemantics()
    {
        using var fixture = new Fixture();
        fixture.Seed(fixture.Home, "{\"Tasks\":[]}", 4);
        var child = Run(fixture, "fleet-capacity");
        Assert.Equal(0, child.ExitCode);
        Assert.Contains("home: available; recorded workers=0, configured local cap=4", child.StandardOutput);
        Assert.Contains("theoretical sum of configured local limits=4", child.StandardOutput);
        AssertLimitations(child.StandardOutput);
    }

    [Fact]
    public void Executable_HeldWriter_ReadsCommittedStateWithoutWritableSetup()
    {
        using var fixture = new Fixture();
        fixture.Seed(fixture.Home, "{\"Tasks\":[{\"LastProcess\":{}}]}", 4);
        var before = fixture.Snapshot();
        using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Home.SqliteStatePath, Pooling = false }.ToString()))
        {
            writer.Open();
            using var begin = writer.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE; UPDATE goals SET snapshot_json='{\"Tasks\":[]}'";
            begin.ExecuteNonQuery();
            try
            {
                using var rival = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = fixture.Home.SqliteStatePath, Pooling = false, DefaultTimeout = 1 }.ToString());
                rival.Open();
                using var probe = rival.CreateCommand();
                probe.CommandText = "BEGIN IMMEDIATE";
                Assert.Equal(5, Assert.Throws<SqliteException>(() => probe.ExecuteNonQuery()).SqliteErrorCode);
                var child = Run(fixture, "fleet-capacity", "--json");
                Assert.Equal(0, child.ExitCode);
                using var document = JsonDocument.Parse(child.StandardOutput);
                Assert.Equal(1, document.RootElement.GetProperty("payload").GetProperty("knownPersistedRunningWorkers").GetInt64());
            }
            finally
            {
                using var rollback = writer.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                rollback.ExecuteNonQuery();
            }
        }
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void Executable_ConflictingExplicitBindingRefusesBeforeCensus()
    {
        using var fixture = new Fixture();
        fixture.Seed(fixture.Home);
        var before = fixture.Snapshot();
        var child = Run(fixture, "fleet-capacity", "--json", "--repository-root", fixture.Repository,
            "--expected-state-directory", Path.Combine(fixture.Root, "wrong-state"));
        Assert.Equal(1, child.ExitCode);
        Assert.Contains("differs from expected", child.StandardError);
        Assert.Equal("", child.StandardOutput);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void Executable_RegistryUnavailable_EmitsUnknownHomeAndMarksInventoryIncomplete()
    {
        using var fixture = new Fixture();
        fixture.Seed(fixture.Home, "{\"Tasks\":[{\"LastProcess\":{}}]}", 5);
        fixture.Register("alpha");
        File.WriteAllText(fixture.Registry.RegistryPath, "{bad registry");
        var before = fixture.Snapshot();
        var child = Run(fixture, "fleet-capacity", "--json");
        Assert.Equal(1, child.ExitCode);
        using var document = JsonDocument.Parse(child.StandardOutput);
        var payload = document.RootElement.GetProperty("payload");
        Assert.False(payload.GetProperty("catalogComplete").GetBoolean());
        Assert.False(payload.GetProperty("complete").GetBoolean());
        Assert.Equal("home", Assert.Single(payload.GetProperty("projects").EnumerateArray()).GetProperty("project").GetString());
        Assert.Equal(0, payload.GetProperty("knownPersistedRunningWorkers").GetInt64());
        Assert.Equal(1, payload.GetProperty("unavailableProjectCount").GetInt32());
        Assert.Equal("registry-unavailable", Assert.Single(payload.GetProperty("errors").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task DirectCommand_FailedRegistryRetainsSuppliedAuthoritativeHomeBinding()
    {
        using var fixture = new Fixture();
        var home = fixture.Home;
        fixture.Seed(home, "{\"Tasks\":[{\"LastProcess\":{}}]}", 5);
        fixture.Register("alpha");
        File.WriteAllText(fixture.Registry.RegistryPath, "{invalid registry");
        var before = fixture.Snapshot();
        using var output = new StringWriter();
        var exit = await CliFleetCapacityQueryCommand.ExecuteAsync(["fleet-capacity", "--json"],
            fixture.Repository, null, fixture.Registry, output, home);
        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(output.ToString());
        var payload = document.RootElement.GetProperty("payload");
        Assert.False(payload.GetProperty("catalogComplete").GetBoolean());
        Assert.Equal(1, payload.GetProperty("knownPersistedRunningWorkers").GetInt64());
        Assert.Equal(0, payload.GetProperty("unavailableProjectCount").GetInt32());
        Assert.Equal("available", Assert.Single(payload.GetProperty("projects").EnumerateArray()).GetProperty("availability").GetString());
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("--json", "--json", null)]
    [InlineData("--unknown", "", "--unknown")]
    [InlineData("--json=true", "", "--json=true")]
    [InlineData("position", "", null)]
    [InlineData("--limit", "2", "--limit")]
    [InlineData("--json", "--zz-not-a-real-flag", "--zz-not-a-real-flag")]
    public void MalformedArguments_ReportUsageBeforeAnyStartup(string first, string second, string? unknownOption)
    {
        using var fixture = new Fixture();
        var home = fixture.Home;
        fixture.Register("alpha");
        File.WriteAllText(fixture.Registry.RegistryPath, "{unreadable registry}");
        string[] args = second.Length == 0 ? ["fleet-capacity", first] : ["fleet-capacity", first, second];
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var error = Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(args));
        Assert.Contains(CliFleetCapacityQueryCommand.Usage, error.Message);
        if (unknownOption is not null)
            Assert.Contains($"Unknown option '{unknownOption}'", error.Message);
        var before = fixture.Snapshot();
        var child = Run(fixture, args);
        Assert.NotEqual(0, child.ExitCode);
        Assert.Contains(CliFleetCapacityQueryCommand.Usage, child.StandardError + child.StandardOutput);
        if (unknownOption is not null)
            Assert.Contains($"Unknown option '{unknownOption}'", child.StandardError + child.StandardOutput);
        Assert.DoesNotContain("registry-unavailable", child.StandardError + child.StandardOutput);
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(Directory.Exists(home.OrchestratorDirectory));
    }

    [Fact]
    public async Task DirectCommand_PreCanceledInvocationPropagatesWithoutOutputOrCreatingPaths()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var output = new StringWriter();
        var before = fixture.Snapshot();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CliFleetCapacityQueryCommand.ExecuteAsync(
            ["fleet-capacity", "--json"], fixture.Repository, null, fixture.Registry, output,
            cancellationToken: cancellation.Token));
        Assert.Equal("", output.ToString());
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task DirectCommand_UsesInvocationClockAndUtcVersionedEnvelope()
    {
        using var fixture = new Fixture();
        fixture.Seed(fixture.Home);
        using var output = new StringWriter();
        var time = new FixedTime(Now.ToOffset(TimeSpan.FromHours(3)));
        var exit = await CliFleetCapacityQueryCommand.ExecuteAsync(["fleet-capacity", "--json"],
            fixture.Repository, null, fixture.Registry, output, time: time);
        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(output.ToString());
        AssertEnvelope(document.RootElement);
        Assert.Equal(Now, document.RootElement.GetProperty("payload").GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(Now, document.RootElement.GetProperty("generatedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task TextAndJson_PreserveAllProjectIdentitiesButBoundPresentationAndErrors()
    {
        using var fixture = new Fixture();
        fixture.Seed(fixture.Home);
        var longLabel = new string('x', 2000) + "\n\t";
        AttentionWorkspaceEntry[] entries = Enumerable.Range(0, 12).Select(index => new AttentionWorkspaceEntry(
            longLabel + index, fixture.Home)).Append(new("home", fixture.Home)).Append(new("unresolved", null, new IOException("private detail"))).ToArray();
        var result = await FleetWorkerCapacityReader.ReadAsync(new(entries, new IOException("private\nregistry detail")), Now);
        var rendered = FleetCapacityTextView.Render(result);
        Assert.All(rendered.Split(Environment.NewLine), line => Assert.True(line.Length < 600, "Text rows must remain bounded."));
        Assert.Contains("+8 more", rendered);
        Assert.Contains("inventory is incomplete", rendered);
        Assert.Contains("Known partial totals", rendered);
        Assert.DoesNotContain("private", rendered);
        Assert.Equal(2, result.Projects.Count);
        var json = AgentReadEnvelope.Serialize("mcg.fleet-capacity", 1, result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.GetProperty("payload").GetProperty("projects").EnumerateArray().ToArray();
        Assert.Equal(12, rows[0].GetProperty("aliases").GetArrayLength());
        Assert.Contains(rows[0].GetProperty("aliases").EnumerateArray(), alias => alias.GetString() == longLabel + "0");
        Assert.Equal("unresolved", rows[1].GetProperty("project").GetString());
    }

    [Fact]
    public void CommandHelpAndClassification_AreAdditive()
    {
        Assert.Contains("fleet-capacity", CliArgumentParser.RecognizedCommands);
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(["fleet-capacity"]));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(["fleet-capacity"]));
        Assert.True(CliCommandHelp.IsCommandSpecificHelp(["fleet-capacity", "--help"]));
        Assert.True(CliCommandHelp.IsCommandSpecificHelp(["attention", "fleet", "--help"]));
        CliCommandHelp.ThrowIfInvalidFlags(["attention", "fleet", "--json"]);
        using var fixture = new Fixture();
        var before = fixture.Snapshot();
        var child = Run(fixture, "fleet-capacity", "--help");
        Assert.Equal(0, child.ExitCode);
        Assert.Contains(CliFleetCapacityQueryCommand.Usage, child.StandardOutput);
        Assert.Equal(before, fixture.Snapshot());
    }

    private static void AssertEnvelope(JsonElement envelope)
    {
        Assert.Equal("mcg.fleet-capacity", envelope.GetProperty("schema").GetString());
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(TimeSpan.Zero, envelope.GetProperty("generatedAt").GetDateTimeOffset().Offset);
        Assert.Equal(TimeSpan.Zero, envelope.GetProperty("payload").GetProperty("observedAt").GetDateTimeOffset().Offset);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("cli-fleet-capacity-").FullName;
        internal string Repository => Path.Combine(Root, "repository");
        internal string Data => Path.Combine(Root, "data");
        internal OrchestratorProjectRegistry Registry { get; }
        internal OrchestratorWorkspace Home => OrchestratorWorkspace.ForDirectory(Repository, null, null, Registry);
        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Repository, ".git"));
            Registry = new(Path.Combine(Root, "registry"), Data);
        }
        internal OrchestratorWorkspace Register(string name)
        {
            Registry.CreateProject(name, Repository);
            return OrchestratorWorkspace.ForProject(name, Repository, dataRootDirectory: Data);
        }
        internal void Seed(OrchestratorWorkspace workspace, string? json = null, int? cap = null)
        {
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            if (json is not null)
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = workspace.SqliteStatePath, Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO goals (id,status,objective,updated_at,snapshot_json) VALUES ('fixture','Completed','fixture','2026-11-03',$json)";
                command.Parameters.AddWithValue("$json", json);
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            if (cap is not null) File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json"),
                (ConductorAutonomyPolicy.Conservative with { MaxConcurrentPaidWorkers = cap.Value }).ToJson());
        }
        // SQLite read coordination files are transient; durable store and registry bytes must remain stable.
        internal string[] Snapshot() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(Root, path) + ":" +
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private static void AssertLimitations(string text)
    {
        Assert.Contains("not confirmed live processes or paid sessions", text);
        Assert.Contains("does not establish persisted process-flag freshness", text);
        Assert.Contains("not a shared host limit or guaranteed available slots", text);
    }

    private static CliChildProcessResult Run(Fixture fixture, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.Repository, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "DOTNET_ROOT", "ProgramFiles", "USERPROFILE", "LOCALAPPDATA" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = fixture.Repository;
        start.Environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = fixture.Registry.RegistryDirectory;
        start.Environment["MCG_ORCHESTRATOR_DATA"] = fixture.Data;
        start.Environment["MCG_ORCHESTRATOR_HOME"] = Path.Combine(fixture.Root, "home");
        start.Environment["MCG_DOTNET_ISOLATED_ROOT"] = Path.Combine(fixture.Root, "dotnet");
        start.Environment["OLLAMA_BASE_URL"] = "http://127.0.0.1:1";
        start.ArgumentList.Add(typeof(OrchestratorWorkspace).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return CliChildProcessRunner.Run(start);
    }
}
