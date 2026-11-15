using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Private repositories, slot files and child environments; no machine-wide slot observations.
[Xunit.Collection("IsolatedProcessSpawningRemainderC")]
public sealed class WorkerBuildCheckQueueDeadlineTests
{
    [Xunit.Fact]
    public async Task HeldSlots_QueueExpires_ReportsCapacityWithoutInvokingCompiler()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var tempBase = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp")
            : Path.GetTempPath();
        var temporaryRoot = Path.GetFullPath(Path.Combine(tempBase, "mcg-queue-" + Guid.NewGuid().ToString("N")));
        var work = Path.Combine(temporaryRoot, "work");
        var shim = Path.Combine(temporaryRoot, "shim");
        var isolated = Path.Combine(temporaryRoot, "isolated");
        var locks = new List<FileStream>();
        try
        {
            Assert.DoesNotContain(".orchestrator-worktrees", temporaryRoot, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(shim);
            Directory.CreateDirectory(Path.Combine(isolated, "build-slots"));
            File.WriteAllText(Path.Combine(work, "One.csproj"), "<Project />");
            var startInfo = Shell(work);
            foreach (var arguments in new[]
            {
                new[] { "init", "--initial-branch=fixture" },
                new[] { "add", "One.csproj" },
                new[] { "-c", "user.name=Build Fixture", "-c", "user.email=fixture@example.invalid",
                    "-c", "commit.gpgsign=false", "-c", "core.hooksPath=", "commit", "-m", "fixture" }
            })
            {
                var git = InfrastructureTestSupport.RunGitProbe(work, arguments,
                    verificationEnvironment: new Dictionary<string, string?>(startInfo.Environment));
                Assert.True(git.Succeeded, git.StandardOutput + git.StandardError);
            }

            var invocationMarker = Path.Combine(shim, "invoked.txt");
            File.WriteAllText(Path.Combine(shim, "dotnet.cmd"),
                "@echo off\r\n> \"%~dp0invoked.txt\" echo invoked\r\nexit /b 97\r\n");
            startInfo.Environment["PATH"] = shim + Path.PathSeparator + startInfo.Environment["PATH"];
            startInfo.Environment["MCG_DOTNET_ISOLATED_ROOT"] = isolated;
            startInfo.Environment["TEMP"] = temporaryRoot;
            startInfo.Environment["TMP"] = temporaryRoot;
            Assert.False(File.Exists(Path.Combine(work, ".orchestrator-context", "worker-build-channel.json")));
            for (var slot = 0; slot < DotnetBuildEnvironmentManager.BuildConcurrencySlotCount; slot++)
            {
                var stream = new FileStream(Path.Combine(isolated, "build-slots", $"build-{slot}.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                try { stream.Lock(0, 1); }
                catch { stream.Dispose(); throw; }
                locks.Add(stream);
            }
            Assert.Equal(DotnetBuildEnvironmentManager.BuildConcurrencySlotCount, locks.Count);
            AddArguments(startInfo, "-File", Path.Combine(repositoryRoot, "scripts", "Invoke-WorkerBuildCheck.ps1"),
                Path.Combine(work, "One.csproj"), "-QueueSeconds", "2");

            var result = await Capture(startInfo);
            var combined = result.Stderr + Environment.NewLine + result.Stdout;
            Assert.True(result.ExitCode == 1, combined);
            var capacity = Assert.Single(result.Stdout.Split('\n')
                .Where(line => line.Contains(WorkerBuildCheckRecoveryText.SlotCapacityOutcome, StringComparison.Ordinal)));
            Assert.Contains(WorkerBuildCheckRecoveryText.SlotCapacityBlock, capacity, StringComparison.Ordinal);
            Assert.Contains("queue_seconds=2;", capacity, StringComparison.Ordinal);
            var wait = Regex.Match(capacity, @"\bwaited_ms=(\d+);", RegexOptions.CultureInvariant);
            Assert.True(wait.Success, capacity);
            // Assert the duration recorded by the queue decision, never the test's elapsed wall time.
            Assert.True(long.Parse(wait.Groups[1].Value, CultureInfo.InvariantCulture) >= 2000, capacity);
            Assert.DoesNotContain(WorkerBuildCheckRecoveryText.BuildSlotLockTimeout, combined, StringComparison.Ordinal);
            Assert.DoesNotContain("FAIL build:", combined, StringComparison.Ordinal);
            Assert.DoesNotContain("PASS build:", combined, StringComparison.Ordinal);
            Assert.DoesNotContain("build-check apparatus failure", combined, StringComparison.Ordinal);
            Assert.False(combined.Split('\n').Any(WorkerBuildLogErrorReader.IsCompilerDiagnostic), combined);
            Assert.False(File.Exists(invocationMarker), "dotnet was invoked before a slot was acquired.");
            Assert.Equal(WorkerBuildNoBuildCause.SlotCapacity, WorkerBuildCheckRecoveryText.NoBuildCause(combined));
            Assert.False(File.Exists(Path.Combine(isolated, "goals", "manual", "artifacts", "worker-build-receipt.json")));
            var journalPath = Assert.Single(Directory.GetFiles(Path.Combine(isolated, "build-slots"),
                "build-slot-holds.jsonl.pending-*.jsonl"));
            using var journal = JsonDocument.Parse(File.ReadAllText(journalPath));
            Assert.Equal("LEASE_WAIT_TIMEOUT", journal.RootElement.GetProperty("event").GetString());
            Assert.Equal("none", journal.RootElement.GetProperty("slot").GetString());
        }
        finally
        {
            foreach (var stream in locks)
            {
                try { stream.Unlock(0, 1); }
                finally { stream.Dispose(); }
            }
            var cleanupPath = Path.GetFullPath(temporaryRoot);
            Assert.Equal(Path.GetFullPath(tempBase).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(cleanupPath));
            Assert.Matches(@"^mcg-queue-[a-f0-9]{32}$", Path.GetFileName(cleanupPath));
            // Git object files may be read-only on Windows; use the shared, loud-failure cleanup.
            SharedTestSupport.RemoveTempDirectory(cleanupPath);
        }
    }

    [Xunit.Fact]
    public async Task FreeSlot_QueueParameterOmitted_PreservesBuildAndReceipt()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var startInfo = Shell(root);
        AddArguments(startInfo, "-File", Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "Fixtures", "WorkerBuildCheckFreeSlot", "Invoke-Fixture.ps1"), "-Scenario", "HashedSlotBusy");
        var result = await Capture(startInfo);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Matches(@"PASS build: 0 errors \(Invoke-WorkerBuildCheck\) projects=1 warnings=0 captured_chars=\d+ elapsed_ms=\d+ logs=", result.Stdout);
        Assert.DoesNotContain(WorkerBuildCheckRecoveryText.SlotCapacityOutcome, result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(WorkerBuildCheckRecoveryText.BuildSlotLockTimeout, result.Stdout + result.Stderr, StringComparison.Ordinal);
        const string prefix = "OBSERVED fixture: ";
        var line = Assert.Single(result.Stdout.Split('\n').Where(value => value.StartsWith(prefix, StringComparison.Ordinal)));
        using var document = JsonDocument.Parse(line[prefix.Length..]);
        var observed = document.RootElement;
        Assert.Equal(0, observed.GetProperty("helperExitCode").GetInt32());
        Assert.EndsWith(Path.Combine("goals", "manual", "artifacts-build-1"),
            observed.GetProperty("artifactsPath").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.True(observed.GetProperty("canonicalReceiptExists").GetBoolean());
        Assert.Equal("success", observed.GetProperty("receipt").GetProperty("buildOutcome").GetString());
        Assert.True(observed.GetProperty("holderRemoved").GetBoolean());
    }

    [Xunit.Fact]
    public async Task OmittedQueueParameter_Uses720Seconds_OnlyForLocalWait()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var helperPath = Path.Combine(root, "scripts", "Invoke-WorkerBuildCheck.ps1");
        const string inspect = """
            $ErrorActionPreference = 'Stop'
            $tokens = $null
            $errors = $null
            $ast = [Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
            if ($errors.Count -ne 0) { throw "Script parse errors: $errors" }
            $parameter = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'QueueSeconds' })
            if ($parameter.Count -ne 1) { throw 'Expected one QueueSeconds parameter' }
            $range = @($parameter[0].Attributes | Where-Object { $_.TypeName.FullName -eq 'ValidateRange' })
            if ($range.Count -ne 1) { throw 'Expected one QueueSeconds range' }
            $comparisons = @($ast.FindAll({ param($node)
                $node -is [BinaryExpressionAst] -and $node.Operator -eq [TokenKind]::Ige -and
                $node.Right -is [VariableExpressionAst] -and $node.Right.VariablePath.UserPath -eq 'QueueSeconds'
            }, $true))
            if ($comparisons.Count -ne 1) { throw 'Expected one local queue comparison' }
            $hostCall = @($ast.FindAll({ param($node)
                $node -is [CommandAst] -and $node.InvocationOperator -eq [TokenKind]::Ampersand -and
                $node.Extent.Text.Contains("'Request-HostWorkerBuild.ps1'")
            }, $true))
            if ($hostCall.Count -ne 1) { throw 'Expected one host handoff' }
            $hostParameters = @($hostCall[0].CommandElements | Where-Object {
                $_ -is [CommandParameterAst]
            } | ForEach-Object { $_.ParameterName })
            # Evaluate the actual comparison with a controlled elapsed value and the parsed default.
            $QueueSeconds = $parameter[0].DefaultValue.SafeGetValue()
            $queueStopwatch = [pscustomobject]@{ Elapsed = [TimeSpan]::FromSeconds(719) }
            $condition = [scriptblock]::Create($comparisons[0].Extent.Text)
            $before = & $condition
            $queueStopwatch.Elapsed = [TimeSpan]::FromSeconds(720)
            $at = & $condition
            [pscustomobject]@{
                Default = $QueueSeconds; Type = $parameter[0].StaticType.FullName
                Range = @($range[0].PositionalArguments | ForEach-Object { $_.SafeGetValue() })
                Before = $before; At = $at; HostParameters = $hostParameters
            } | ConvertTo-Json -Compress
            """;
        var startInfo = Shell(root);
        var command = "using namespace System.Management.Automation.Language\n$path = '" +
            helperPath.Replace("'", "''", StringComparison.Ordinal) + "'\n" + inspect;
        AddArguments(startInfo, "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        var result = await Capture(startInfo);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var observed = document.RootElement;
        Assert.Equal(720, observed.GetProperty("Default").GetInt32());
        Assert.Equal("System.Int32", observed.GetProperty("Type").GetString());
        Assert.Equal(new[] { 1, 2147483 }, observed.GetProperty("Range").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.False(observed.GetProperty("Before").GetBoolean());
        Assert.True(observed.GetProperty("At").GetBoolean());
        Assert.Equal(new[] { "Projects", "Configuration" },
            observed.GetProperty("HostParameters").EnumerateArray().Select(value => value.GetString()).ToArray());
    }

    private static ProcessStartInfo Shell(string workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(info.Environment, workingDirectory);
        AddArguments(info, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass");
        return info;
    }

    private static void AddArguments(ProcessStartInfo info, params string[] arguments)
    {
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
    }

    private static Task<TestChildProcessCapture.Result> Capture(ProcessStartInfo info) => Task.Run(() =>
        // Hang-only bound also covers the reused free-slot fixture's 270-second child guard.
        TestChildProcessCapture.Run(info, hangBound: TimeSpan.FromSeconds(600), start: startInfo =>
        {
            var process = Process.Start(startInfo);
            process?.StandardInput.Close();
            return process;
        }));
}
