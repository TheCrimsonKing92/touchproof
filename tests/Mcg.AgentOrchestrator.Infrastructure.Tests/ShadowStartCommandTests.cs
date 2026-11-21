using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Microsoft.Data.Sqlite;

// Parallel-safe. Each test owns its temporary tree and the supervised candidate process.
public sealed class ShadowStartCommandTests
{
    internal const string LegacyMarker = "legacy-grouped-attempt-without-lease";

    [Fact]
    public void LegacyGroupedAttempt_StartupFailureIncludesCandidateOutput()
    {
        using var fixture = new Fixture();
        fixture.CreateDatabase("cohort-acceptance.db", marker: LegacyMarker);
        fixture.CreateDatabase("state.db");

        var failure = fixture.Run("throw", "failed");
        var success = fixture.Run("normal", "normal");

        Assert.NotEqual(0, failure.Exit);
        Assert.Contains("assertion=startup-first-tick", failure.Error);
        Assert.Contains("startup failure", failure.Error);
        Assert.Contains($"stderr: Legacy grouped attempt has no lease in {fixture.Scratch("failed")}", failure.Error);
        Assert.Contains("wall_ms=", failure.Error);
        Assert.Contains("peak_working_set_bytes=", failure.Error);
        Assert.Equal(0, success.Exit);
        Assert.Contains("result=pass", success.Output);
        Assert.Contains("wall_ms=", success.Output);
        Assert.Contains("peak_working_set_bytes=", success.Output);
        var launch = File.ReadAllLines(Path.Combine(fixture.Scratch("normal"), "received.txt"));
        Assert.Contains("--no-dispatch", launch);
        Assert.Contains(fixture.Scratch("normal"), launch);
        Assert.Contains(Path.Combine(fixture.Scratch("normal"), ".orchestrator"), launch);
        Assert.DoesNotContain(launch, value => value.Contains(fixture.Source, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["project=", "tenant="], launch.Where(value =>
            value.StartsWith("project=", StringComparison.Ordinal) || value.StartsWith("tenant=", StringComparison.Ordinal)));
    }

    [Fact]
    public void LateFirstTick_FailsStartupCeilingWhileNormalStartupPasses()
    {
        using var fixture = new Fixture();
        var failure = fixture.Run("slow", "late", startupSeconds: "8");
        var success = fixture.Run("normal", "normal", startupSeconds: "8");

        Assert.NotEqual(0, failure.Exit);
        Assert.Contains("assertion=startup-ceiling", failure.Error);
        Assert.Contains("wall_ms=", failure.Error);
        Assert.Contains("peak_working_set_bytes=", failure.Error);
        Assert.Equal(0, success.Exit);
        Assert.Contains("result=pass", success.Output);
    }

    [Theory]
    [InlineData("stdout-tick", "tick-advance")]
    [InlineData("wrong-stop", "stop-file-loop-stop")]
    [InlineData("bad-exit", "exit-code")]
    public void MissingJournalOrExitEvidence_FailsNamedAssertion(string mode, string assertion)
    {
        using var fixture = new Fixture();
        var result = fixture.Run(mode, "scratch");

        Assert.NotEqual(0, result.Exit);
        Assert.Contains($"assertion={assertion}", result.Error);
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("shadow-start-").FullName;
        private readonly string _script;
        internal string Source { get; }

        internal Fixture()
        {
            Source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
            _script = Path.Combine(_root, "candidate.ps1");
            File.WriteAllText(_script, CandidateScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        internal string Scratch(string name) => Path.Combine(_root, name);

        internal void CreateDatabase(string relativePath, string journalMode = "delete", string marker = "fixture")
        {
            var path = Path.Combine(Source, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Pooling = false
            }.ToString());
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = $"PRAGMA journal_mode={journalMode}; CREATE TABLE grouped_attempts (label TEXT, lease TEXT); INSERT INTO grouped_attempts VALUES ($marker, NULL);";
            create.Parameters.AddWithValue("$marker", marker);
            create.ExecuteNonQuery();
        }

        internal (int Exit, string Output, string Error) Run(string mode, string scratch, string startupSeconds = "20")
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exit = CliShadowStartCommand.Run(
                ["shadow-start", "--source", Source, "--scratch", Scratch(scratch),
                 "--candidate", OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
                 "--candidate-arg=-NoProfile", "--candidate-arg=-NonInteractive",
                 "--candidate-arg=-ExecutionPolicy", "--candidate-arg=Bypass",
                 "--candidate-arg=-File", $"--candidate-arg={_script}", $"--candidate-arg={mode}",
                 "--startup-ceiling-seconds", startupSeconds, "--budget-seconds", "60"],
                output, error);
            return (exit, output.ToString(), error.ToString());
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private const string CandidateScript = """
        $ErrorActionPreference = 'Stop'
        trap {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        }
        $mode = $args[0]
        $root = (Get-Location).Path
        $state = Join-Path $root '.orchestrator'
        $rootIndex = [Array]::IndexOf($args, '--repository-root')
        $stateIndex = [Array]::IndexOf($args, '--expected-state-directory')
        if ($args -notcontains '--no-dispatch' -or $args -notcontains '--disposable-fixture' -or
            $rootIndex -lt 0 -or $args[$rootIndex + 1] -ne $root -or
            $stateIndex -lt 0 -or $args[$stateIndex + 1] -ne $state -or
            $env:MCG_ORCHESTRATOR_REPOSITORY_ROOT -ne $root) { exit 3 }
        [IO.File]::WriteAllLines((Join-Path $root 'received.txt'), [string[]]($args + @(
            $root, $env:MCG_ORCHESTRATOR_REPOSITORY_ROOT,
            "project=$env:MCG_ORCHESTRATOR_PROJECT", "tenant=$env:MCG_ORCHESTRATOR_TENANT")))
        $logs = [IO.Directory]::CreateDirectory((Join-Path $state 'logs')).FullName
        $journal = Join-Path $logs 'conduct-events.log'
        $utf8 = New-Object Text.UTF8Encoding($false)
        function Write-Event($kind, $detail) {
            $line = @{ timestamp = [DateTimeOffset]::UtcNow.ToString('O'); eventKind = $kind; detail = $detail } |
                ConvertTo-Json -Compress
            [IO.File]::AppendAllText($journal, $line + [Environment]::NewLine, $utf8)
        }
        Write-Event 'loop-start' 'LOOP_START'
        if ($mode -eq 'throw') {
            $bytes = [IO.File]::ReadAllBytes((Join-Path $state 'cohort-acceptance.db'))
            if ([Text.Encoding]::UTF8.GetString($bytes).Contains('legacy-grouped-attempt-without-lease') -and
                -not (Test-Path (Join-Path $state 'legacy-build.lease'))) {
                throw "Legacy grouped attempt has no lease in $root"
            }
        }
        if ($mode -eq 'slow') { Start-Sleep -Seconds 30 }
        Write-Event 'startup-first-tick' 'STARTUP_FIRST_TICK'
        Write-Event 'tick' 'TICK tick=1'
        if ($mode -eq 'stdout-tick') {
            Write-Output 'TICK tick=2'
            exit 0
        }
        Write-Event 'tick' 'TICK tick=2'
        $stop = Join-Path $root '.conduct-stop'
        while (-not (Test-Path $stop)) { Start-Sleep -Milliseconds 25 }
        if ($mode -eq 'wrong-stop') { Write-Event 'loop-stop' 'LOOP_STOP tick=2 reason=max-iter' }
        else { Write-Event 'loop-stop' 'LOOP_STOP tick=2 reason=stop-file' }
        if ($mode -eq 'bad-exit') { exit 7 }
        exit 0
        """;
}
