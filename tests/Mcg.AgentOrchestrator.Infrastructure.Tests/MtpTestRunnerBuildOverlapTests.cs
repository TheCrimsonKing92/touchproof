using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// Parallel-safe. Each test owns its temporary roots, mutex keys, and child processes.
public sealed class MtpTestRunnerBuildOverlapTests
{
    private const string ReceiptPrefix = "BUILD LOCK RECEIPT - ";
    private const string Project = "Probe.csproj";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EquivalentRootsSerializeAndKeepTheLockAcrossRetry(bool retry)
    {
        using var fixture = new BuildFixture();
        var root = fixture.CreateRoot("worktree");
        await using var holder = fixture.Start(root, gateBuild: true);
        await holder.ExpectAsync("build:1");

        var equivalentRoot = Path.Combine(root, "..", "worktree") + Path.DirectorySeparatorChar;
        if (OperatingSystem.IsWindows())
            equivalentRoot = equivalentRoot.ToUpperInvariant();

        await using var waiter = fixture.Start(
            equivalentRoot, retry ? "LogThenSuccess" : "Success", gateBuild: true, observeContention: true);
        await waiter.ExpectAsync("contended");
        AssertWorktreeLocked(root);
        holder.Release();
        await holder.CompleteAsync(expectedResult: true);

        await waiter.ExpectAsync("build:1");
        AssertWorktreeLocked(root);
        waiter.Release();
        if (retry)
        {
            await waiter.ExpectAsync("delay:500");
            AssertWorktreeLocked(root);
            waiter.Release();
            await waiter.ExpectAsync("build:2");
            AssertWorktreeLocked(root);
            waiter.Release();
        }
        await waiter.CompleteAsync(expectedResult: true);

        Assert.Empty(holder.Receipts);
        AssertReceipt(waiter, root, retry ? "succeeded" : "not-attempted", contended: true);
    }

    [Fact]
    public async Task DifferentRootsCanBothEnterBuildBeforeEitherExits()
    {
        using var fixture = new BuildFixture();
        await using var first = fixture.Start(fixture.CreateRoot("first"), gateBuild: true);
        await first.ExpectAsync("build:1");
        await using var second = fixture.Start(fixture.CreateRoot("second"), gateBuild: true);
        await second.ExpectAsync("build:1");

        first.Release();
        second.Release();
        await first.CompleteAsync(expectedResult: true);
        await second.CompleteAsync(expectedResult: true);
        Assert.Empty(first.Receipts);
        Assert.Empty(second.Receipts);
    }

    [Fact]
    public async Task FailedBuildReleasesLockWhileItsInvokingProcessIsStillAlive()
    {
        using var fixture = new BuildFixture();
        var root = fixture.CreateRoot("worktree");
        await using var failed = fixture.Start(root, "OtherError", gateBuild: true, keepAlive: true);
        await failed.ExpectAsync("build:1");
        AssertWorktreeLocked(root);
        failed.Release();
        await failed.ExpectAsync("result:False");
        Assert.Contains(BuildFailure(17), failed.Lines);

        await using var next = fixture.Start(root, gateBuild: true);
        await next.ExpectAsync("build:1");
        Assert.False(failed.HasExited);
        next.Release();
        await next.CompleteAsync(expectedResult: true);
        Assert.Empty(next.Receipts);

        failed.Release();
        await failed.ExpectExitAsync();
    }

    [Theory]
    [InlineData("LogThenSuccess", 2, true, "succeeded")]
    [InlineData("CaptureThenSuccess", 2, true, "succeeded")]
    [InlineData("Twice", 2, false, "failed")]
    [InlineData("ThenOtherError", 2, false, "failed")]
    [InlineData("OtherError", 1, false, null)]
    [InlineData("Stale", 1, false, null)]
    [InlineData("SimilarCode", 1, false, null)]
    [InlineData("SuccessfulDiagnostic", 1, true, null)]
    public async Task OnlyCurrentFailedAttemptWithCs2012RetriesOnce(
        string scenario, int attempts, bool succeeds, string? retryStatus)
    {
        using var fixture = new BuildFixture();
        var root = fixture.CreateRoot("worktree");
        await using var build = fixture.Start(root, scenario);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt == 2)
                await build.ExpectAsync("delay:500");
            await build.ExpectAsync($"build:{attempt}");
        }
        await build.CompleteAsync(succeeds);

        Assert.Equal(succeeds ? 0 : 1, build.Lines.Count(line => line.StartsWith("BUILD FAILURE - ", StringComparison.Ordinal)));
        if (!succeeds)
            Assert.Contains(BuildFailure(attempts == 2 ? 31 : 17), build.Lines);
        if (retryStatus is null)
            Assert.Empty(build.Receipts);
        else
            AssertReceipt(build, root, retryStatus, contended: false);
    }

    [Fact]
    public void OperatorScriptsKeepNodeReuseDisabledAndNeverShutDownBuildServers()
    {
        var scripts = Path.Combine(VerifiedRepositoryRoot.Find(), "scripts");
        var module = File.ReadAllText(Path.Combine(scripts, "MtpTestRunner.psm1"));
        var summary = File.ReadAllText(Path.Combine(scripts, "Invoke-TestSummary.ps1"));
        const string shutdown = """(?ix)\bbuild-server\b['"]?(?:\s|,|`)+['"]?\bshutdown\b""";
        Assert.DoesNotMatch(shutdown, module);
        Assert.DoesNotMatch(shutdown, summary);

        var arguments = Regex.Match(module,
            """(?ms)^function Invoke-MtpBuild \{\s*(?:(?!^function ).)*?^\s*\$arguments = \[string\[\]\]@\(\s*\$DotnetPath,\s*'build',(?<args>.*?)^\s*\)(?:(?!^function ).)*?Invoke-MtpBuildProcess -Executable \$DotnetPath -Arguments \$arguments\b""");
        Assert.True(arguments.Success, "Expected the dotnet build argument list in Invoke-MtpBuild.");
        Assert.Matches("""(?m)^\s*'-nodeReuse:false',?\s*$""", arguments.Groups["args"].Value);
    }

    private static string BuildFailure(int exitCode) =>
        $"BUILD FAILURE - 'test-dotnet build' exited {exitCode}. The managed MTP runner was not launched.";

    private static void AssertReceipt(BuildChild child, string root, string retryStatus, bool contended)
    {
        var receipt = Assert.Single(child.Receipts);
        var prefix = $"{ReceiptPrefix}worktree='{root}' project='{Path.Combine(root, Project)}' waitMs=";
        Assert.StartsWith(prefix, receipt, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var duration = contended ? "[0-9]+" : "0";
        Assert.Matches($"^{duration} retry={retryStatus}$", receipt[prefix.Length..]);
    }

    private static void AssertWorktreeLocked(string root)
    {
        var lockKey = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lockKey)));
        var scope = OperatingSystem.IsWindows() ? @"Global\" : string.Empty;
        using var mutex = Mutex.OpenExisting($"{scope}Mcg.MtpBuild.{hash}");
        var acquired = mutex.WaitOne(0);
        if (acquired)
            mutex.ReleaseMutex();
        Assert.False(acquired, "The worktree mutex must be owned by the child at this checkpoint.");
    }

    private sealed class BuildFixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("mcg-mtp-overlap-").FullName;
        private readonly string _module = Path.Combine(VerifiedRepositoryRoot.Find(), "scripts", "MtpTestRunner.psm1");

        public string CreateRoot(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

        public BuildChild Start(
            string root, string scenario = "Success", bool gateBuild = false,
            bool observeContention = false, bool keepAlive = false)
        {
            var run = CreateRoot(Guid.NewGuid().ToString("N")[..8]);
            var script = Path.Combine(run, "InvokeBuild.ps1");
            File.WriteAllText(script, ChildScript);
            var start = new ProcessStartInfo(TestPowerShell.Executable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _root
            };
            foreach (var arg in new[]
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                _module, root, run, scenario, gateBuild ? "gate" : "run",
                observeContention ? "observe" : "ignore", keepAlive ? "stay" : "exit"
            })
                start.ArgumentList.Add(arg);

            return new BuildChild(Process.Start(start) ?? throw new InvalidOperationException("Could not start the build probe."));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class BuildChild : IAsyncDisposable
    {
        private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);
        private readonly Process _process;
        private readonly Task<string> _stderr;
        private readonly List<string> _lines = [];

        public BuildChild(Process process)
        {
            _process = process;
            _stderr = process.StandardError.ReadToEndAsync();
        }

        public bool HasExited => _process.HasExited;
        public IReadOnlyList<string> Lines => _lines;
        public IEnumerable<string> Receipts => _lines.Where(line => line.StartsWith(ReceiptPrefix, StringComparison.Ordinal));

        public async Task ExpectAsync(string signal)
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync().WaitAsync(HangGuard) is { } line)
                {
                    _lines.Add(line);
                    if (!line.StartsWith("event:", StringComparison.Ordinal))
                        continue;
                    Assert.Equal($"event:{signal}", line);
                    return;
                }
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException($"Build child did not signal '{signal}'.\n{string.Join('\n', _lines)}", exception);
            }

            Assert.Fail($"Build child exited before '{signal}'.\n{string.Join('\n', _lines)}\n{await _stderr.WaitAsync(HangGuard)}");
        }

        public void Release() => _process.StandardInput.WriteLine("release");

        public async Task CompleteAsync(bool expectedResult)
        {
            await ExpectAsync($"result:{expectedResult}");
            await ExpectExitAsync();
        }

        public async Task ExpectExitAsync()
        {
            await _process.WaitForExitAsync().WaitAsync(HangGuard);
            var stderr = await _stderr.WaitAsync(HangGuard);
            Assert.True(_process.ExitCode == 0, $"Build child exited {_process.ExitCode}.\n{stderr}");
            Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
            Assert.Null(await _process.StandardOutput.ReadLineAsync().WaitAsync(HangGuard));
        }

        public async ValueTask DisposeAsync()
        {
            using (_process)
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(HangGuard);
            }
        }
    }

    private const string ChildScript = """
        param($ModulePath, $Root, $Run, $Scenario, $Gate, $Observe, $Lifetime)
        $ErrorActionPreference = 'Stop'
        $module = Import-Module -Name $ModulePath -PassThru
        $result = & $module {
            param($Root, $Run, $Scenario, $Gate, $Observe)
            $script:scenario = $Scenario
            $script:gate = $Gate
            $script:attempt = 0

            function Wait-BuildProbeGate {
                if ($script:gate -eq 'gate' -and [Console]::ReadLine() -ne 'release') {
                    throw 'Expected a build probe release.'
                }
            }

            function Invoke-MtpBuildProcess {
                param($Executable, $Arguments, $OutputLog, $WorkingDirectory, $StartupHookPath)
                $script:attempt++
                [Console]::WriteLine("event:build:$script:attempt")
                Wait-BuildProbeGate
                $loggerArgument = $Arguments | Where-Object { $_ -like '-flp:LogFile=*' }
                $log = $loggerArgument.Substring('-flp:LogFile='.Length).Split(';')[0]
                $exitCode = 0
                switch ($script:scenario) {
                    'LogThenSuccess' {
                        if ($script:attempt -eq 1) {
                            [IO.File]::WriteAllText($log, 'CSC : error CS2012: file in use')
                            [IO.File]::WriteAllText($OutputLog, 'no compiler diagnostics in capture')
                            $exitCode = 17
                        }
                    }
                    'CaptureThenSuccess' {
                        if ($script:attempt -eq 1) {
                            [IO.File]::WriteAllText($OutputLog, 'CSC : error cs2012: file in use')
                            $exitCode = 17
                        }
                    }
                    'Twice' {
                        [IO.File]::WriteAllText($OutputLog, 'CSC : error CS2012: file in use')
                        $exitCode = if ($script:attempt -eq 1) { 17 } else { 31 }
                    }
                    'ThenOtherError' {
                        $code = if ($script:attempt -eq 1) { 'CS2012' } else { 'CS2001' }
                        [IO.File]::WriteAllText($log, "CSC : error ${code}: build failed")
                        $exitCode = if ($script:attempt -eq 1) { 17 } else { 31 }
                    }
                    'OtherError' {
                        [IO.File]::WriteAllText($log, 'CSC : error CS2001: missing source')
                        $exitCode = 17
                    }
                    'Stale' { $exitCode = 17 }
                    'SimilarCode' {
                        [IO.File]::WriteAllText($OutputLog, 'CSC : error CS20120: unrelated')
                        $exitCode = 17
                    }
                    'SuccessfulDiagnostic' {
                        [IO.File]::WriteAllText($log, 'CS2012 diagnostic example')
                    }
                }
                [pscustomobject]@{
                    Started = $true
                    StartFailureMessage = $null
                    MonitoringFailureMessage = $null
                    CleanupConfirmed = $true
                    DrainConfirmed = $true
                    ExitCode = $exitCode
                }
            }

            function Start-Sleep {
                param([int]$Milliseconds)
                [Console]::WriteLine("event:delay:$Milliseconds")
                Wait-BuildProbeGate
            }

            if ($Scenario -eq 'Stale') {
                foreach ($name in @('build-Probe.log', 'build-Probe-capture.log')) {
                    [IO.File]::WriteAllText((Join-Path $Run $name), 'previous attempt: error CS2012')
                }
            }
            if ($Observe -eq 'observe') {
                # Observe the blocking branch without substituting the OS mutex.
                $build = (Get-Command Invoke-MtpBuild).ScriptBlock
                $blockingWait = $build.Ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
                        $node.Member.Extent.Text -eq 'WaitOne' -and
                        ($null -eq $node.Arguments -or $node.Arguments.Count -eq 0)
                }, $false)
                if ($null -eq $blockingWait) { throw 'Could not locate the contended mutex wait.' }
                $null = Set-PSBreakpoint -Script $build.File -Line $blockingWait.Extent.StartLineNumber -Action {
                    [Console]::WriteLine('event:contended')
                }
            }
            Invoke-MtpBuild -RepositoryRoot $Root -Projects @(@{ project = 'Probe.csproj' }) `
                -Configuration Debug -DotnetPath test-dotnet -RunDirectory $Run
        } $Root $Run $Scenario $Gate $Observe
        if ($result -isnot [bool]) { throw "Expected a Boolean result. Got: $result" }
        [Console]::WriteLine("event:result:$result")
        if ($Lifetime -eq 'stay' -and [Console]::ReadLine() -ne 'release') {
            throw 'Expected a failed-caller release.'
        }
        """;
}
