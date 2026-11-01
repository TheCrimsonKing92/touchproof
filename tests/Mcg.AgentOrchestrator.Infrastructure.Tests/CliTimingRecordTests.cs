using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each child has an explicit environment and its own repository/data roots.
public sealed class CliTimingRecordTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManagedExitRecordsOnlyTheVerbAndWriteFailurePreservesResults(bool error)
    {
        using var fixture = new Fixture();
        string[] args = error ? ["repo-process-info", "secret-value"] : ["help", "operator-commands", "secret-value"];
        var success = fixture.Run(args);
        Assert.Equal(error ? 1 : 0, success.ExitCode);
        var line = Assert.Single(File.ReadAllLines(fixture.TimingPath));
        using var document = JsonDocument.Parse(line);
        var record = document.RootElement;
        Assert.Equal(args[0], record.GetProperty("verb").GetString());
        Assert.DoesNotContain("secret-value", line);
        Assert.DoesNotContain("operator-commands", line);
        Assert.Equal("fresh", record.GetProperty("launcher_path").GetString());
        Assert.Equal(success.ExitCode, record.GetProperty("exit_code").GetInt32());
        Assert.Equal(JsonValueKind.Number, record.GetProperty("app_elapsed_ms").ValueKind);
        Assert.Equal(JsonValueKind.Number, record.GetProperty("launcher_elapsed_ms").ValueKind);
        Assert.True(record.GetProperty("timestamp").TryGetDateTimeOffset(out _));

        Directory.Delete(fixture.Logs, true);
        File.WriteAllText(fixture.Logs, "logs directory is unavailable");
        Assert.True(File.Exists(fixture.Logs));
        var failedWrite = fixture.Run(args);
        Assert.Equal(success, failedWrite);
        Assert.Equal("logs directory is unavailable", File.ReadAllText(fixture.Logs));
    }

    [Fact]
    public void NoArgumentsRecordsEmptyVerbEvenWhenStartupValidationFails()
    {
        using var fixture = new Fixture();
        var result = fixture.Run([], invalidProject: true);
        Assert.Equal(1, result.ExitCode);
        using var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(fixture.TimingPath)));
        Assert.Equal("", json.RootElement.GetProperty("verb").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public void DirectInvocationPreservesNoncreatingBehavior()
    {
        using var fixture = new Fixture();
        Assert.Equal(0, fixture.Run(["--help"], launcher: false).ExitCode);
        Assert.False(Directory.Exists(fixture.Logs));
    }

    [Fact]
    public void InvalidMetadataIsUnclassifiedAndRotationRetainsWholeRecords()
    {
        using var fixture = new Fixture();
        var completed = new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(fixture.Logs);
        File.WriteAllText(fixture.TimingPath, new string(' ', 1_048_576));
        CliTimingRecorder.Write(fixture.Logs, "", "unknown", "invalid", completed, 123, 7);
        Assert.Single(Directory.GetFiles(fixture.Logs, "cli-timing-*.jsonl"));
        using var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(fixture.TimingPath)));
        Assert.Equal("", json.RootElement.GetProperty("verb").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("launcher_path").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("launcher_elapsed_ms").ValueKind);
        Assert.Equal(123, json.RootElement.GetProperty("app_elapsed_ms").GetDouble());
        Assert.Equal(7, json.RootElement.GetProperty("exit_code").GetInt32());
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "cli-timing-" + Guid.NewGuid().ToString("N"));
        internal string Logs => Path.Combine(Root, ".orchestrator", "logs");
        internal string TimingPath => Path.Combine(Logs, "cli-timing.jsonl");
        internal Fixture() => Directory.CreateDirectory(Root);
        internal CliChildProcessResult Run(string[] args, bool launcher = true, bool invalidProject = false)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment.Clear();
            foreach (var key in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "DOTNET_ROOT", "ProgramFiles", "USERPROFILE", "LOCALAPPDATA" })
                if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
            start.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = Root;
            start.Environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = Path.Combine(Root, "registry");
            start.Environment["MCG_ORCHESTRATOR_DATA"] = Path.Combine(Root, "data");
            start.Environment["MCG_ORCHESTRATOR_HOME"] = Path.Combine(Root, "home");
            start.Environment["OLLAMA_BASE_URL"] = "http://127.0.0.1:1";
            if (launcher)
            {
                start.Environment[CliTimingRecorder.PathVariable] = "fresh";
                start.Environment[CliTimingRecorder.StartedVariable] = "2026-11-01T00:00:00Z";
            }
            if (invalidProject) start.Environment[OrchestratorProjectSelection.ProjectEnvironmentVariable] = "invalid/name";
            start.ArgumentList.Add(typeof(OrchestratorWorkspace).Assembly.Location);
            foreach (var arg in args) start.ArgumentList.Add(arg);
            return CliChildProcessRunner.Run(start);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
