using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: each test owns its child environment, repositories, worktree and artifacts.
// No shared collection: the real lane script takes its own mutex around worktree creation.
public sealed class ExecutorLaneRunnerDiagnosisScriptTests : IDisposable
{
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();
    private const string Lane = "runner-diagnosis";
    private const string Project = "tests/Fixture/Fixture.csproj";
    private const string TrxName = "lane-results.trx";
    private const string MissingBanner = "=== COMPLETED WITH MISSING TRX ===";
    private const string RetainedPath = @"C:\runner diagnostics\missing-trx";
    private const string RetainedLine = "Retained diagnostic directory: " + RetainedPath;

    [Fact]
    public async Task MissingTrxExit26_RecordsDiagnosisRetainedDirectoryAndFoundCount()
    {
        var sha = Seed($"""
            Write-Output '{MissingBanner}'
            Write-Output '{RetainedLine}'
            exit 26
            """);
        var result = await Run(Job(sha));
        Assert.True(result.Exit == 0, result.Error);
        Assert.False(File.Exists(Path.Combine(_root, "job.error.txt")));
        var statusPath = Path.Combine(ResultFolder(sha), "status.json");
        using var document = JsonDocument.Parse(File.ReadAllText(statusPath));
        var status = document.RootElement;
        Assert.Equal("completed", status.GetProperty("state").GetString());
        Assert.Equal(26, status.GetProperty("exitCode").GetInt32());
        Assert.Empty(status.GetProperty("trx").EnumerateArray());
        Assert.Equal(new[] { MissingBanner, RetainedLine },
            status.GetProperty("runnerDiagnosis").EnumerateArray().Select(line => line.GetString()));
        Assert.Equal(RetainedPath, status.GetProperty("retainedDirectory").GetString());
        Assert.Equal(0, status.GetProperty("trxFoundCount").GetInt32());
        var completion = SshRemoteLaneStatus.Read(statusPath)!.Completion();
        Assert.Equal(26, completion.ExitCode);
        Assert.Empty(completion.Names);
    }

    [Fact]
    public async Task ManyBanners_CapsAtEightTruncatesAndKeepsLastRetainedDirectory()
    {
        var longBanner = "=== RUNNER/TOOLING FAILURE " + new string('x', 400);
        var lastDirectory = @"C:\runner diagnostics\" + new string('y', 350);
        string[] lines =
        [
            "ordinary output", "runner/tooling failure", "prefix Retained diagnostic directory: ignored",
            longBanner, "  Retained diagnostic directory: C:\\first  ",
            "=== CLEANUP FAILURE ===", "=== TEST HOST TIMED OUT ===", MissingBanner,
            "CLEANUP FAILURE repeated", "CLEANUP FAILURE repeated", "CLEANUP FAILURE repeated",
            "CLEANUP FAILURE ninth", "CLEANUP FAILURE tenth",
            "  Retained diagnostic directory: " + lastDirectory + "  "
        ];
        var body = string.Join(Environment.NewLine, lines.Select(line => "Write-Output '" + line + "'")) + "\n" + """
            $nested = Join-Path $ResultsRoot 'nested'
            New-Item -ItemType Directory -Force -Path $nested | Out-Null
            Set-Content -LiteralPath (Join-Path $nested 'lane-results.trx') -Value '<TestRun />'
            exit 26
            """;
        var sha = Seed(body);
        var result = await Run(Job(sha));
        Assert.True(result.Exit == 0, result.Error);
        Assert.False(File.Exists(Path.Combine(_root, "job.error.txt")));
        var statusPath = Path.Combine(ResultFolder(sha), "status.json");
        using var document = JsonDocument.Parse(File.ReadAllText(statusPath));
        var status = document.RootElement;
        Assert.Equal("completed", status.GetProperty("state").GetString());
        Assert.Equal(26, status.GetProperty("exitCode").GetInt32());
        Assert.Equal(lines.Skip(3).Take(8).Select(line => line[..Math.Min(300, line.Length)]),
            status.GetProperty("runnerDiagnosis").EnumerateArray().Select(line => line.GetString()));
        Assert.Equal(lastDirectory, status.GetProperty("retainedDirectory").GetString());
        Assert.Equal(1, status.GetProperty("trxFoundCount").GetInt32());
        Assert.Equal(new[] { TrxName }, status.GetProperty("trx").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal(File.ReadAllBytes(Path.Combine(RunnerResults(sha), "nested", TrxName)),
            File.ReadAllBytes(Path.Combine(ResultFolder(sha), TrxName)));
        var completion = SshRemoteLaneStatus.Read(statusPath)!.Completion();
        Assert.Equal(26, completion.ExitCode);
        Assert.Equal(new[] { TrxName }, completion.Names);
    }

    [Fact]
    public async Task SuccessWithTrx_KeepsExistingRecordAndCopiesTrx()
    {
        const string trx = "<TestRun id=\"success-control\" />";
        var sha = Seed($"""
            New-Item -ItemType Directory -Force -Path $ResultsRoot | Out-Null
            [IO.File]::WriteAllText((Join-Path $ResultsRoot '{TrxName}'), '{trx}')
            Write-Output '{MissingBanner}'
            Write-Output '{RetainedLine}'
            exit 0
            """);

        var result = await Run(Job(sha));

        Assert.True(result.Exit == 0, result.Error);
        Assert.False(File.Exists(Path.Combine(_root, "job.error.txt")));
        var statusPath = Path.Combine(ResultFolder(sha), "status.json");
        using var document = JsonDocument.Parse(File.ReadAllText(statusPath));
        AssertExistingRecord(document.RootElement, sha, 0, [TrxName]);
        var source = Path.Combine(RunnerResults(sha), TrxName);
        Assert.Equal(trx, File.ReadAllText(source));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(ResultFolder(sha), TrxName)));
        var completion = SshRemoteLaneStatus.Read(statusPath)!.Completion();
        Assert.Equal(0, completion.ExitCode);
        Assert.Equal(new[] { TrxName }, completion.Names);
    }

    [Fact]
    public async Task UnreadableRunnerLog_WritesExistingRecordAndExitsAsBefore()
    {
        var sha = Seed($"""
            Write-Output '{MissingBanner}'
            Write-Output '{RetainedLine}'
            exit 26
            """);
        Directory.CreateDirectory(ResultFolder(sha));
        var logPath = Path.Combine(ResultFolder(sha), "runner.log");
        File.WriteAllText(logPath, "");
        // Share writes so Tee-Object can populate the log, but deny the lane's diagnostic reader.
        // Hold the denial across the whole job; no timing or interprocess pacing is needed.
        using var log = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.Write);
        var denied = Assert.Throws<IOException>(() => File.ReadAllText(logPath));
        Assert.Equal(32, denied.HResult & 0xffff); // ERROR_SHARING_VIOLATION

        var result = await Run(Job(sha));

        Assert.True(result.Exit == 0, result.Error);
        Assert.False(File.Exists(Path.Combine(_root, "job.error.txt")));
        denied = Assert.Throws<IOException>(() => File.ReadAllText(logPath));
        Assert.Equal(32, denied.HResult & 0xffff);
        Assert.True(log.Length > 0, "The runner tee must have written through the read-denying handle.");
        using var reader = new StreamReader(log, leaveOpen: true);
        var output = await reader.ReadToEndAsync();
        Assert.Contains(MissingBanner, output);
        Assert.Contains(RetainedLine, output);
        var statusPath = Path.Combine(ResultFolder(sha), "status.json");
        using var document = JsonDocument.Parse(File.ReadAllText(statusPath));
        AssertExistingRecord(document.RootElement, sha, 26, []);
        var completion = SshRemoteLaneStatus.Read(statusPath)!.Completion();
        Assert.Equal(26, completion.ExitCode);
        Assert.Empty(completion.Names);
    }

    private static void AssertExistingRecord(JsonElement status, string sha, int exitCode, string[] trx)
    {
        string[] fields = ["sha", "lane", "project", "filter", "state", "host", "utc", "attemptId", "executorId",
            "filterHash", "mainSha", "manifestIdentity", "commitSha", "treeSha", "testSettings",
            "exitCode", "seconds", "trx", "phases"];
        Assert.Equal(fields.Order(StringComparer.Ordinal),
            status.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("completed", status.GetProperty("state").GetString());
        Assert.Equal(sha, status.GetProperty("sha").GetString());
        Assert.Equal(sha, status.GetProperty("commitSha").GetString());
        Assert.Equal(Lane, status.GetProperty("lane").GetString());
        Assert.Equal(Project, status.GetProperty("project").GetString());
        Assert.Equal("FullyQualifiedName~Fixture", status.GetProperty("filter").GetString());
        Assert.Equal(exitCode, status.GetProperty("exitCode").GetInt32());
        Assert.Equal(trx, status.GetProperty("trx").EnumerateArray().Select(name => name.GetString()));
        Assert.True(status.GetProperty("seconds").GetDouble() >= 0);
        var phases = status.GetProperty("phases");
        string[] durations = ["worktreeSeconds", "buildWaitSeconds", "buildSeconds", "testSeconds"];
        Assert.Equal(durations.Append("buildSkipped").Order(StringComparer.Ordinal),
            phases.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.False(phases.GetProperty("buildSkipped").GetBoolean());
        foreach (var name in durations)
            Assert.True(phases.GetProperty(name).GetDouble() >= 0, name + " must remain a non-negative number.");
        foreach (var name in new[] { "runnerDiagnosis", "retainedDirectory", "trxFoundCount" })
            Assert.False(status.TryGetProperty(name, out _), name + " must be absent from the existing record.");
    }

    private string ResultFolder(string sha) => Path.Combine(_root, "results", sha[..9], Lane);

    private string RunnerResults(string sha) =>
        Path.Combine(_root, "local-app-data", "Temp", "Low", "mcg-tests", "exec-" + sha[..9] + "-" + Lane);

    private static Dictionary<string, object> Job(string sha) => new()
    {
        ["sha"] = sha, ["project"] = Project, ["filter"] = "FullyQualifiedName~Fixture", ["lane"] = Lane,
        ["attemptId"] = "diagnosis-fixture", ["executorId"] = "fixture-executor",
        ["filterHash"] = "fixture-filter", ["mainSha"] = "", ["manifestIdentity"] = "diagnosis:v1"
    };

    private string Seed(string runnerBody)
    {
        var tools = Path.Combine(_root, "tools");
        Directory.CreateDirectory(Path.Combine(tools, "bin"));
        foreach (var name in new[] { "Invoke-ExecutorJob.ps1", "Invoke-ExecutorLane.ps1" })
        {
            var source = Path.Combine(VerifiedRepositoryRoot.Find(), "scripts", "executor", "tools", name);
            Assert.True(File.Exists(source), "Tracked executor script is required: " + name);
            File.Copy(source, Path.Combine(tools, name));
        }
        // The sampler writes nothing, keeping optional resource fields out of the status controls.
        File.WriteAllText(Path.Combine(tools, "Measure-LaneResources.ps1"), "exit 0");
        File.WriteAllText(Path.Combine(tools, "bin", "pwsh.cmd"),
            $"@echo off\r\n\"{TestPowerShell.Executable}\" %*\r\nexit /b %errorlevel%\r\n");
        var seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(Path.Combine(seed, "scripts"));
        Git(seed, "init", "-q");
        Git(seed, "config", "user.name", "Runner diagnosis fixture");
        Git(seed, "config", "user.email", "fixture@mcg.invalid");
        File.WriteAllText(Path.Combine(seed, "scripts", "Invoke-WorkerBuildDiagnostic.ps1"), "exit 0");
        File.WriteAllText(Path.Combine(seed, "scripts", "Invoke-TestSummary.ps1"), """
            param($Target, $FilterFile, [switch]$NoBuild, $ResultsRoot, $TestHostTimeoutSeconds, $TestSettingsFile)
            $ErrorActionPreference = 'Stop'
            if (-not $NoBuild -or $Target -ne 'tests/Fixture/Fixture.csproj' -or
                (Get-Content -Raw -LiteralPath $FilterFile) -ne 'FullyQualifiedName~Fixture') {
                throw 'Stub runner did not receive the lane invocation.'
            }
            """ + "\n" + runnerBody);
        Git(seed, "add", "-A");
        Git(seed, "commit", "-q", "-m", "stub lane runner");
        Directory.CreateDirectory(Path.Combine(_root, "repo"));
        Git(_root, "clone", "-q", "--bare", seed, Path.Combine(_root, "repo", "repo.git"));
        return Git(seed, "rev-parse", "HEAD");
    }

    private static string Git(string root, params string[] args)
    {
        var result = GitCli.RunForRepository(root, args);
        Assert.True(result.Succeeded && !result.DrainTimedOut, result.Error);
        return result.Output.Trim();
    }

    private async Task<(int Exit, string Error)> Run(Dictionary<string, object> job)
    {
        var executable = TestPowerShell.Executable;
        var programFiles = Directory.GetParent(executable)?.Parent?.Parent?.FullName;
        Assert.True(programFiles is not null && string.Equals(
            Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"), executable, StringComparison.OrdinalIgnoreCase),
            "The real executor lane requires a resolved PowerShell/7/pwsh.exe installation: " + executable);
        var path = Path.Combine(_root, "job.json");
        File.WriteAllText(path, JsonSerializer.Serialize(job));
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        string[] allow = ["COMSPEC", "PATH", "PATHEXT", "PROGRAMDATA", "PROGRAMFILES(X86)",
            "SYSTEMDRIVE", "SYSTEMROOT", "TEMP", "TMP", "WINDIR"];
        var required = start.Environment.Where(item => allow.Contains(item.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
        start.Environment.Clear();
        foreach (var item in required) start.Environment[item.Key] = item.Value;
        start.Environment["USERPROFILE"] = _root;
        start.Environment["LOCALAPPDATA"] = Path.Combine(_root, "local-app-data");
        start.Environment["PROGRAMFILES"] = programFiles;
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File",
                     Path.Combine(_root, "tools", "Invoke-ExecutorJob.ps1"), "-JobPath", path, "-RunRoot", _root })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120)); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Missing event: executor lane job process exited");
        }
        return (process.ExitCode, (await stderr) + (await stdout));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
