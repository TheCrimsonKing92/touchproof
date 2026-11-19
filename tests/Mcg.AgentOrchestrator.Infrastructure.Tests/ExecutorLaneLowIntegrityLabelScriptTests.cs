using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test controls its own repository, child environment, and results tree.
public sealed class ExecutorLaneLowIntegrityLabelScriptTests : IDisposable
{
    private const string Lane = "low-label";
    private const string Project = "tests/Fixture/Fixture.csproj";
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    private string Tools => Path.Combine(_root, "tools");
    private string Bin => Path.Combine(Tools, "bin");
    private string LocalAppData => Path.Combine(_root, "local app data");
    private string LowTemp => Path.Combine(LocalAppData, "Temp", "Low");
    private string McgTests => Path.Combine(LowTemp, "mcg-tests");
    private string QueryCall => $"query \"{LowTemp}\"";
    private string SetCall => $"label \"{LowTemp}\" /setintegritylevel \"(OI)(CI)low\"";

    [Fact]
    public async Task LaneLabelsParentBeforeResultsAndLeavesExistingLowLabelAlone()
    {
        var sha = SeedLane();
        Assert.False(Directory.Exists(LowTemp));

        var first = await RunLane(sha);

        Assert.True(first.ExitCode == 0, first.StandardError + first.StandardOutput);
        Assert.Equal(new[] { QueryCall, SetCall }, ReadLabelCalls());
        var results = Path.Combine(McgTests, $"exec-{sha[..9]}-{Lane}");
        Assert.True(File.Exists(Path.Combine(results, "fixture.trx")));

        var second = await RunLane(sha);

        Assert.True(second.ExitCode == 0, second.StandardError + second.StandardOutput);
        Assert.Equal(new[] { QueryCall, SetCall, QueryCall }, ReadLabelCalls());
    }

    [Fact]
    public async Task FailedLabelStopsLaneBeforeResultsAndReportsPathAndExitCode()
    {
        var sha = SeedLane();
        File.WriteAllText(Path.Combine(Bin, "fail-label"), "");

        var result = await RunLane(sha);

        Assert.Equal(new[] { QueryCall, SetCall }, ReadLabelCalls());
        Assert.NotEqual(0, result.ExitCode);
        var output = Path.Combine(_root, "results", sha[..9], Lane);
        using var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "status.json")));
        Assert.Equal("failed", status.RootElement.GetProperty("state").GetString());
        var message = status.RootElement.GetProperty("error").GetString();
        Assert.Contains(LowTemp, message, StringComparison.Ordinal);
        Assert.Contains("icacls.exe exited with code 5", message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(McgTests));
        Assert.False(File.Exists(Path.Combine(output, "runner.log")));
    }

    private string[] ReadLabelCalls()
    {
        var log = Path.Combine(Bin, "icacls.log");
        Assert.True(File.Exists(log), "The lane did not run the fixture's icacls.exe shim.");
        return File.ReadAllLines(log);
    }

    private string SeedLane()
    {
        Directory.CreateDirectory(Bin);
        var repo = VerifiedRepositoryRoot.Find();
        var source = Path.Combine(repo, "scripts", "executor", "tools", "Invoke-ExecutorLane.ps1");
        File.Copy(source, Path.Combine(Tools, "Invoke-ExecutorLane.ps1"));
        File.WriteAllText(Path.Combine(Tools, "Measure-LaneResources.ps1"), "exit 0");
        File.WriteAllText(Path.Combine(Bin, "pwsh.cmd"),
            $"@echo off\r\n\"{TestPowerShell.Executable}\" -ExecutionPolicy Bypass %*\r\nexit /b %errorlevel%\r\n");
        File.WriteAllText(Path.Combine(Bin, "icacls.exe.cmd"), """
            @echo off
            if /i "%~2"=="/setintegritylevel" goto label
            >>"%~dp0icacls.log" echo query "%~1"
            if not exist "%~1\." exit /b 11
            if exist "%~dp0low-label" echo     Mandatory Label\Low Mandatory Level:^(I^)^(NW^)
            exit /b 0
            :label
            >>"%~dp0icacls.log" echo label "%~1" %~2 "%~3"
            if not exist "%~1\." exit /b 11
            if exist "%~1\mcg-tests" exit /b 12
            if exist "%~dp0fail-label" exit /b 5
            >"%~dp0low-label" echo low
            exit /b 0
            """.ReplaceLineEndings("\r\n") + "\r\n");

        var seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(Path.Combine(seed, "scripts"));
        File.WriteAllText(Path.Combine(seed, "scripts", "Invoke-WorkerBuildDiagnostic.ps1"), "exit 0");
        File.WriteAllText(Path.Combine(seed, "scripts", "Invoke-TestSummary.ps1"), """
            param($Target, $FilterFile, [switch]$NoBuild, $ResultsRoot, $TestHostTimeoutSeconds)
            $ErrorActionPreference = 'Stop'
            if (-not $NoBuild -or -not (Test-Path -LiteralPath $ResultsRoot -PathType Container)) {
                throw 'The lane must create the results directory before invoking the runner.'
            }
            Set-Content -LiteralPath (Join-Path $ResultsRoot 'fixture.trx') -Value '<TestRun />'
            """);
        Git(seed, "init", "-q");
        Git(seed, "add", "--", "scripts");
        Git(seed, "-c", "user.name=Lane fixture", "-c", "user.email=fixture@mcg.invalid",
            "-c", "commit.gpgsign=false", "commit", "-q", "-m", "Stub executor lane runner");
        var sha = Git(seed, "rev-parse", "HEAD");
        Directory.CreateDirectory(Path.Combine(_root, "runs"));
        Directory.Move(seed, Path.Combine(_root, "runs", sha[..9]));
        return sha;
    }

    private static string Git(string root, params string[] args)
    {
        var result = GitCli.RunForRepository(root, args);
        Assert.True(result.Succeeded && !result.DrainTimedOut, result.Error);
        return result.Output.Trim();
    }

    private Task<CliChildProcessResult> RunLane(string sha)
    {
        var executable = TestPowerShell.Executable;
        var programFiles = Directory.GetParent(executable)?.Parent?.Parent?.FullName;
        Assert.True(programFiles is not null && string.Equals(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"),
            executable, StringComparison.OrdinalIgnoreCase), "The real lane uses the PowerShell 7 installation: " + executable);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        string[] allow = ["COMSPEC", "PATH", "PATHEXT", "PROGRAMDATA", "PROGRAMFILES(X86)", "SYSTEMDRIVE", "SYSTEMROOT", "WINDIR"];
        var required = start.Environment.Where(item => allow.Contains(item.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
        start.Environment.Clear();
        foreach (var item in required) start.Environment[item.Key] = item.Value;
        start.Environment["USERPROFILE"] = _root;
        start.Environment["LOCALAPPDATA"] = LocalAppData;
        start.Environment["PROGRAMFILES"] = programFiles;
        start.Environment["TEMP"] = _root;
        start.Environment["TMP"] = _root;
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                     Path.Combine(Tools, "Invoke-ExecutorLane.ps1"), "-Sha", sha, "-Project", Project,
                     "-Filter", "FullyQualifiedName~Fixture", "-Lane", Lane, "-RunRoot", _root })
            start.ArgumentList.Add(argument);
        return CliChildProcessRunner.RunAsync(start);
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
