using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsGitDirectLaunchProcessTreeTests
{
    [Fact]
    public async Task Version_FromWindowlessConsole_CreatesOnlyTheRealGit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var document = await GitDirectLaunchTestHarness.Run("version");
        var report = document.RootElement;
        GitDirectLaunchTestHarness.AssertWindowless(report);
        Assert.False(report.GetProperty("cacheCreatedBefore").GetBoolean());
        Assert.True(report.GetProperty("cacheCreatedAfter").GetBoolean());
        Assert.Equal(1u, report.GetProperty("after").GetUInt32() - report.GetProperty("before").GetUInt32());
        Assert.Equal(GitDirectLaunchTestHarness.ExpectedRealGit(report), report.GetProperty("image").GetString(), ignoreCase: true);
        Assert.NotEqual(report.GetProperty("shim").GetString(), report.GetProperty("image").GetString());
        Assert.Equal(HandleListProcessLauncher.UnicodeEnvironment | HandleListProcessLauncher.ExtendedStartup,
            report.GetProperty("flags").GetUInt32());
        Assert.StartsWith("git version", report.GetProperty("Output").GetString()!);

        // Same job-accounting apparatus must detect the shim plus its extra console host.
        using var control = await GitDirectLaunchTestHarness.Run("shim-version");
        var shim = control.RootElement;
        GitDirectLaunchTestHarness.AssertWindowless(shim);
        Assert.Equal(3u, shim.GetProperty("after").GetUInt32() - shim.GetProperty("before").GetUInt32());
        Assert.Equal(shim.GetProperty("shim").GetString(), shim.GetProperty("image").GetString(), ignoreCase: true);
    }
}

internal static class GitDirectLaunchTestHarness
{
    internal static async Task<JsonDocument> Run(string operation)
    {
        var directory = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var reportPath = Path.Combine(directory, "report.json");
            var probeDirectory = FixtureProbeLocator.FindDirectory(
                typeof(ProcessTreeGuiSuppressionTestsGitDirectLaunchProcessTreeTests),
                "Mcg.AgentOrchestrator.ConsoleIoProbe", "Mcg.AgentOrchestrator.ConsoleIoProbe.dll")
                ?? throw new FileNotFoundException("Console I/O probe was not built.");
            var dotnet = HandleListProcessLauncher.ResolveApplication("dotnet");
            var info = new ProcessStartInfo(dotnet)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory
            };
            foreach (var argument in new[] { Path.Combine(probeDirectory, "Mcg.AgentOrchestrator.ConsoleIoProbe.dll"),
                         "--git-direct-launch", reportPath, operation })
                info.ArgumentList.Add(argument);
            foreach (var name in new[] { "DOTNET_STARTUP_HOOKS", ChildConsoleLaunchPolicy.OffSwitchVariable,
                         "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                         "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT" })
                info.Environment.Remove(name);
            var shim = HandleListProcessLauncher.ResolveApplication("git");
            info.Environment["PATH"] = string.Join(';', Path.GetDirectoryName(shim), Path.GetDirectoryName(dotnet), Environment.SystemDirectory);
            info.Environment["MSYSTEM"] = "inherited-invalid-system";
            info.Environment["HOME"] = directory;
            info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            info.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, "absent-global-config");
            var stdout = Path.Combine(directory, "stdout.txt");
            var stderr = Path.Combine(directory, "stderr.txt");
            using var start = OwnedProcessGroup.StartSuspendedContainedWithFileCapture(info, stdout, stderr, requestOwnConsole: true);
            using var group = start.Group;
            using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            guard.CancelAfter(TimeSpan.FromMinutes(3));
            start.Resume();
            try
            {
                await start.WaitForOwnedExitAsync(guard.Token);
                var output = File.ReadAllText(stdout);
                var errors = File.ReadAllText(stderr);
                Assert.True(File.Exists(reportPath), $"Git launch probe report missing: stdout={output} stderr={errors}");
                var json = File.ReadAllText(reportPath);
                Assert.True(start.TryReadOwnedExitCode() == 0, $"Git launch probe failed: {json} stdout={output} stderr={errors}");
                var report = JsonDocument.Parse(json);
                Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("error").ValueKind);
                Assert.Equal(0, report.RootElement.GetProperty("ExitCode").GetInt32());
                Assert.True(report.RootElement.GetProperty("ProcessStarted").GetBoolean());
                Assert.False(report.RootElement.GetProperty("DrainTimedOut").GetBoolean());
                return report;
            }
            catch (OperationCanceledException exception) when (guard.IsCancellationRequested)
            {
                throw new TimeoutException("Git launch measurement host did not exit and publish its report.", exception);
            }
            finally
            {
                if (start.TryReadOwnedExitCode() is null) group.Kill();
            }
        }
        finally { SharedTestSupport.RemoveTempDirectory(directory); }
    }

    internal static void AssertWindowless(JsonElement report)
    {
        Assert.Equal(0, report.GetProperty("consoleWindow").GetInt64());
        Assert.True(report.GetProperty("consoleProcessCount").GetUInt32() > 0, report.GetRawText());
    }

    internal static string ExpectedRealGit(JsonElement report)
    {
        var shim = report.GetProperty("shim").GetString()!;
        Assert.Equal("cmd", Path.GetFileName(Path.GetDirectoryName(shim)), ignoreCase: true);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(shim))!;
        var candidates = new[] { "mingw64", "mingw32", "clangarm64" }
            .Select(system => Path.Combine(root, system, "bin", "git.exe")).Where(File.Exists).ToArray();
        // The acceptance host must have a standard, unambiguous Git for Windows installation.
        return Assert.Single(candidates);
    }
}
