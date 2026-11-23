using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

public sealed class FixtureGitReplayScriptTests
{
    [Fact]
    public void Replay_WithControlledRunnerAndTrace_RetainsArtifactsAndWritesReceipt()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Directory.CreateTempSubdirectory("mcg-fixture-replay-").FullName;
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var sourceScripts = Path.Combine(VerifiedRepositoryRoot.Find(), "scripts");
            foreach (var name in new[]
            {
                "Invoke-FixtureGitReplay.ps1", "FixtureGitReplayWindows.cs.txt",
                "MtpTestRunner.psm1", "WorkerBuildReceipt.psm1"
            })
            {
                File.Copy(Path.Combine(sourceScripts, name), Path.Combine(scripts, name));
            }

            File.WriteAllText(Path.Combine(scripts, "Invoke-TestSummary.ps1"), ControlledRunner);
            var harness = Path.Combine(scripts, "Run-Fixture.ps1");
            File.WriteAllText(harness, ControlledNativeInputs);
            var evidence = Path.Combine(root, "evidence");
            var profile = Path.Combine(root, "app");
            var start = new ProcessStartInfo
            {
                FileName = TestPowerShell.Executable,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment["LOCALAPPDATA"] = profile;
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness, evidence })
            {
                start.ArgumentList.Add(argument);
            }

            var result = TestChildProcessCapture.Run(start, hangBound: TimeSpan.FromSeconds(60));
            Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);

            var lowRoot = Path.Combine(profile, "Temp", "Low", "mcg-tests");
            Assert.Empty(Directory.EnumerateFileSystemEntries(lowRoot));
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "replay.json")));
            var receipt = document.RootElement;
            Assert.Equal(root, receipt.GetProperty("repository").GetString());
            Assert.Equal("Release", receipt.GetProperty("configuration").GetString());
            Assert.Equal(2, receipt.GetProperty("native_git").GetInt32());
            Assert.Equal(100L, receipt.GetProperty("cpu_100ns").GetInt64());
            Assert.Collection(receipt.GetProperty("selections").EnumerateArray(),
                selection => AssertSelection(selection, "goal-worktree", 8),
                selection => AssertSelection(selection, "seeded-factory", 1));

            var artifacts = receipt.GetProperty("artifacts").EnumerateArray()
                .ToDictionary(artifact => artifact.GetProperty("path").GetString()!);
            foreach (var family in new[] { "goal-worktree", "seeded-factory" })
            {
                foreach (var name in new[] { Path.Combine("tests", "controlled.trx"), "slot.jsonl", "probes.jsonl", "process.etl", "runner.log", "capture.json" })
                {
                    var path = Path.Combine(family, name);
                    var bytes = File.ReadAllBytes(Path.Combine(evidence, path));
                    Assert.NotEmpty(bytes);
                    Assert.Equal(bytes.LongLength, artifacts[path].GetProperty("bytes").GetInt64());
                    Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), artifacts[path].GetProperty("sha256").GetString());
                }
            }
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static void AssertSelection(JsonElement selection, string family, int cases)
    {
        Assert.Equal(family, selection.GetProperty("family").GetString());
        Assert.Equal(cases, selection.GetProperty("executed_cases").GetInt32());
        Assert.Equal(1, selection.GetProperty("shared_probe").GetInt32());
        Assert.Equal(0, selection.GetProperty("unattributed").GetInt32());
    }

    private const string ControlledNativeInputs = """
        param([string]$EvidenceDirectory)
        $ErrorActionPreference = 'Stop'

        function git.exe {
            $global:LASTEXITCODE = 0
            if ($args[0] -eq '--version') { return 'git version fixture' }
            switch ($args[2]) {
                'rev-parse' { return 'controlled-revision' }
                'status' { return }
                'merge-base' { return }
                'remote' { return 'fixture://repository' }
                'log' { return 'controlled-parent' }
                default { throw "Unexpected Git arguments: $args" }
            }
        }
        function dotnet.exe {
            $global:LASTEXITCODE = 0
            'controlled-sdk'
        }
        # MTP owns the real directory and ownership sidecar. Only the OS label query is stubbed.
        function global:icacls.exe {
            $global:LASTEXITCODE = 0
            'Mandatory Label\Low Mandatory Level:'
        }
        function logman.exe {
            if ($args[0] -ne 'create') { throw "Unexpected trace command: $args" }
            $path = $args[[array]::IndexOf($args, '-o') + 1]
            @'
        <Trace>
          <Event><EventData>
            <Data Name="ProcessID">101</Data><Data Name="ParentProcessID">100</Data>
            <Data Name="ProcessSequenceNumber">1</Data><Data Name="ParentProcessSequenceNumber">0</Data>
            <Data Name="CreateTime">1000</Data><Data Name="ImageName">pwsh.exe</Data>
          </EventData></Event>
          <Event><EventData>
            <Data Name="ProcessID">102</Data><Data Name="ParentProcessID">101</Data>
            <Data Name="ProcessSequenceNumber">2</Data><Data Name="ParentProcessSequenceNumber">1</Data>
            <Data Name="CreateTime">1001</Data><Data Name="ImageName">git.exe</Data>
          </EventData></Event>
        </Trace>
        '@ | Set-Content -LiteralPath $path -Encoding utf8
            $global:LASTEXITCODE = 0
        }
        function Get-WinEvent {
            param([string]$Path, [switch]$Oldest, [string]$FilterXPath)
            foreach ($node in ([xml](Get-Content -Raw -LiteralPath $Path)).Trace.Event) {
                $event = [pscustomobject]@{ ProviderName = 'Microsoft-Windows-Kernel-Process'; Xml = $node.OuterXml }
                $event | Add-Member -MemberType ScriptMethod -Name ToXml -Value { $this.Xml } -PassThru |
                    Add-Member -MemberType ScriptMethod -Name Dispose -Value {} -PassThru
            }
        }

        Add-Type -TypeDefinition @'
        using System;
        using System.Diagnostics;

        public static class FixtureGitReplayWindows
        {
            public static Replay Run(string executable, string encoded, string directory, int timeoutSeconds)
            {
                var start = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true
                };
                foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded })
                    start.ArgumentList.Add(argument);
                using var runner = Process.Start(start) ?? throw new InvalidOperationException("Controlled runner did not start.");
                if (!runner.WaitForExit(timeoutSeconds * 1000))
                {
                    runner.Kill(entireProcessTree: true);
                    throw new TimeoutException("Controlled runner did not exit.");
                }
                return new Replay(runner.ExitCode);
            }

            public static TraceLoss StopTrace(string session) => new TraceLoss(0, 0, 0);
            public sealed record TraceLoss(int EventsLost, int FileBuffersLost, int RealTimeBuffersLost);
            public sealed record Replay(int ExitCode)
            {
                public int RootPid => 101;
                public long RootStartFileTime => 1000;
                public int ProcessCount => 2;
                public long UserCpu100ns => 30;
                public long KernelCpu100ns => 20;
            }
        }
        '@
        & (Join-Path $PSScriptRoot 'Invoke-FixtureGitReplay.ps1') -EvidenceDirectory $EvidenceDirectory -Configuration Release
        """;

    private const string ControlledRunner = """
        param([string]$Target, [string]$Configuration, [switch]$NoBuild, [string]$Filter,
            [string]$ResultsRoot, [string]$SlotTimingFile, [string]$TestInput)
        $ErrorActionPreference = 'Stop'
        $staging = Split-Path -Parent $ResultsRoot
        $lowRoot = Join-Path $env:LOCALAPPDATA 'Temp\Low\mcg-tests'
        if ((Split-Path -Parent $staging) -ne $lowRoot -or
            -not (Test-Path -LiteralPath (Join-Path $staging '.mtp-run-ownership.json')) -or
            $SlotTimingFile -ne (Join-Path $staging 'slot.jsonl') -or
            $TestInput -ne ('MCG_TEST_INPUT_FIXTURE_GIT_PROBE_TRACE=' + (Join-Path $staging 'probes.jsonl')) -or
            -not $NoBuild -or $Configuration -ne 'Release') {
            throw "Unexpected replay runner arguments or staging directory: $PSBoundParameters"
        }
        $factory = 'WorkerDispatchTestsSeededRepositoryFactoryTests'
        $factoryMethod = 'Create_MultipleCopies_ProducesIndependentCommittedRepositories'
        $worktree = 'GoalWorktreeTestsRebaseMergeNoHistoryRewrite'
        switch ($Filter) {
            "FullyQualifiedName=$worktree" {
                $class = $worktree
                $methods = @(
                    'MergeIntegratedHistory_PreservesEarlierGoalCommitsAndExactTree'
                    'ExistingSquashPath_NegativeControlDropsReviewedHeadFromAncestry'
                    'OverlappingConflict_AbortsAndLeavesBranchAndCheckoutUnchanged'
                    'AlreadyContainsMain_LeavesBranchUnchanged'
                    'DirtyWorktree_PreservesBranchAndUncommittedContent'
                    'AdditiveConflict_MergesWithOriginalHeadAsFirstParent'
                    'MissingBranch_ReturnsRecoveryWithoutCreatingBranch'
                    'MissingWorktree_LeavesExistingBranchUnchanged'
                )
            }
            "FullyQualifiedName=$factory&Name~$factoryMethod" {
                $class = $factory
                $methods = @($factoryMethod)
            }
            default { throw "Unexpected replay selection: $Filter" }
        }
        [void][IO.Directory]::CreateDirectory($ResultsRoot)
        $definitions = foreach ($method in $methods) {
            "<UnitTest id='$method'><TestMethod className='$class' name='$method'/></UnitTest>"
        }
        $results = foreach ($method in $methods) {
            "<UnitTestResult testId='$method' testName='$method' outcome='Passed'/>"
        }
        "<TestRun><TestDefinitions>$definitions</TestDefinitions><Results>$results</Results></TestRun>" |
            Set-Content -LiteralPath (Join-Path $ResultsRoot 'controlled.trx') -Encoding utf8
        foreach ($method in $methods) {
            $launches = [int]($method -eq $methods[0])
            @{
                test_name = "$class.$method"; process_id = 101
                git_launches = $launches; git_fixture_probe_launches = $launches
                git_subcommands = @{ init = $launches }; git_callers = @{ 'Controlled<-Runner|init' = $launches }
            } | ConvertTo-Json -Compress | Add-Content -LiteralPath $SlotTimingFile -Encoding utf8
        }
        @{
            test_name = "$class.$($methods[0])"; process_id = 101
            child_process_id = 102; child_start_filetime = 1001
            caller = 'Controlled<-Runner'; subcommand = 'init'; arguments = @('init')
        } | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $staging 'probes.jsonl') -Encoding utf8
        Write-Output 'Controlled replay runner completed'
        exit 0
        """;
}
