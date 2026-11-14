using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawningRemainderC")]
public sealed class WorkerBuildCheckGoalLeaseOwnerTests
{
    private const string GoalId = "f15194077b1a4b06b776cf6f5fdab678";
    private const string RepositoryRoot = @"C:\lease fixture\repository";
    private const string AttemptName = "acceptance-seeded-attempt";

    [Xunit.Fact]
    public async Task LiveForeignOwnerLeaseIsLeftByteIdentical()
    {
        using var observation = await RunFixtureAsync("LiveOwner");
        var observed = observation.RootElement;
        Assert.True(observed.GetProperty("ownerAliveAfterRun").GetBoolean());
        var before = Convert.FromBase64String(observed.GetProperty("beforeBase64").GetString()!);
        var after = Convert.FromBase64String(observed.GetProperty("afterBase64").GetString()!);
        Assert.Equal(before, after);
        using var lease = JsonDocument.Parse(after);
        var metadata = lease.RootElement;
        var ownerId = observed.GetProperty("seededOwnerId").GetInt32();
        Assert.True(ownerId > 0);
        Assert.NotEqual(observed.GetProperty("helperProcessId").GetInt32(), ownerId);
        Assert.Equal(ownerId, metadata.GetProperty("ownerProcessId").GetInt32());
        Assert.Equal(AttemptName, metadata.GetProperty("lastAttemptName").GetString());
        Assert.Equal(GoalId, metadata.GetProperty("goalId").GetString());
        Assert.Equal(RepositoryRoot, metadata.GetProperty("repositoryRoot").GetString());
    }

    [Xunit.Fact]
    public async Task ExitedOwnerLeaseIsReplacedAndKeepsGoalIdentityFields()
    {
        using var observation = await RunFixtureAsync("ExitedOwner");
        var observed = observation.RootElement;
        Assert.True(observed.GetProperty("ownerExitedBeforeRun").GetBoolean());
        using var before = JsonDocument.Parse(Convert.FromBase64String(observed.GetProperty("beforeBase64").GetString()!));
        Assert.Equal(observed.GetProperty("seededOwnerId").GetInt32(), before.RootElement.GetProperty("ownerProcessId").GetInt32());
        Assert.Equal(AttemptName, before.RootElement.GetProperty("lastAttemptName").GetString());
        using var lease = JsonDocument.Parse(Convert.FromBase64String(observed.GetProperty("afterBase64").GetString()!));
        var metadata = lease.RootElement;
        var helperId = observed.GetProperty("helperProcessId").GetInt32();
        Assert.True(helperId > 0);
        Assert.Equal(helperId, metadata.GetProperty("ownerProcessId").GetInt32());
        Assert.Equal("worker-build-check", metadata.GetProperty("lastAttemptName").GetString());
        Assert.Equal(GoalId, metadata.GetProperty("goalId").GetString());
        Assert.Equal(RepositoryRoot, metadata.GetProperty("repositoryRoot").GetString());
    }

    private static async Task<JsonDocument> RunFixtureAsync(string scenario)
    {
        var repositoryRoot = FindRepositoryRoot();
        var fixturePath = Path.Combine(repositoryRoot, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "Fixtures", "WorkerBuildCheckGoalLeaseOwner", "Invoke-Fixture.ps1");
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", fixturePath,
            "-Scenario", scenario, "-GoalId", GoalId, "-SeedRepositoryRoot", RepositoryRoot, "-SeedAttemptName", AttemptName
        })
            startInfo.ArgumentList.Add(argument);

        // Hang-only launcher bound exceeds the fixture's child guard and setup.
        var capture = await Task.Run(() => TestChildProcessCapture.Run(
            startInfo, hangBound: TimeSpan.FromSeconds(600), start: info =>
            {
                var process = Process.Start(info);
                process?.StandardInput.Close();
                return process;
            }));
        Assert.True(capture.ExitCode == 0, capture.Stdout + Environment.NewLine + capture.Stderr);
        Assert.Contains("PASS build:", capture.Stdout, StringComparison.Ordinal);
        const string prefix = "OBSERVED fixture: ";
        var line = Assert.Single(capture.Stdout.Split('\n').Where(value => value.StartsWith(prefix, StringComparison.Ordinal)));
        var observation = JsonDocument.Parse(line[prefix.Length..]);
        Assert.Equal(0, observation.RootElement.GetProperty("helperExitCode").GetInt32());
        return observation;
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
