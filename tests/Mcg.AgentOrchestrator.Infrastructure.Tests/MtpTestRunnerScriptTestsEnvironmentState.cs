using System.Diagnostics;
using System.Text.Json;

// All environment mutations live in an owned child, never in the test host.
[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpTestRunnerScriptTestsEnvironmentState(ITestOutputHelper output)
{
    [Fact]
    public void Scrub_DisallowedStates_RemovesKeysAndPreservesInputs()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var nonempty = "MCG_ENV_PUBLIC_NONEMPTY_" + suffix;
        var empty = "MCG_ENV_PUBLIC_EMPTY_" + suffix;
        var input = "MCG_TEST_INPUT_ENV_" + suffix;
        var dotnet = "DOTNET_MTP_PUBLIC_" + suffix;
        var nuget = "NUGET_MTP_PUBLIC_" + suffix;
        string[] names = [nonempty, empty, input, "TEMP", dotnet, nuget];
        using var receipt = RunCase(sandbox, names, $$"""
            [Environment]::SetEnvironmentVariable('{{nonempty}}', 'inherited-public', 'Process')
            [Environment]::SetEnvironmentVariable('{{empty}}', '', 'Process')
            [Environment]::SetEnvironmentVariable('{{input}}', 'inherited-input', 'Process')
            [Environment]::SetEnvironmentVariable('{{dotnet}}', 'allowed-dotnet', 'Process')
            [Environment]::SetEnvironmentVariable('{{nuget}}', 'allowed-nuget', 'Process')
            $seeded = Read-PublicEnvironment $names
            $inputs = Assert-MtpTestInputEnvironment -TestInput @('{{input}}= explicit=public ')
            Set-MtpHermeticEnvironment -RepositoryRoot $root -WritableRoot $root -TestInputEnvironment $inputs
            @{ seeded = $seeded; local = (Read-PublicEnvironment $names); child = (Read-ChildEnvironment) }
            """);
        var seeded = receipt.RootElement.GetProperty("seeded");
        AssertState(seeded, nonempty, "inherited-public");
        AssertState(seeded, empty, "");
        AssertState(seeded, input, "inherited-input");
        foreach (var observation in Observations(receipt))
        {
            AssertState(observation, nonempty, null);
            AssertState(observation, empty, null);
            AssertState(observation, input, " explicit=public ");
            AssertState(observation, "TEMP", sandbox.Root);
            AssertState(observation, dotnet, "allowed-dotnet");
            AssertState(observation, nuget, "allowed-nuget");
        }
    }

    [Fact]
    public void Restore_OriginalStates_PreservesMembershipAndValues()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var prefix = "MCG_ENV_PUBLIC_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var absent = prefix + "_ABSENT";
        var empty = prefix + "_EMPTY";
        var nonempty = prefix + "_NONEMPTY";
        var temporary = prefix + "_TEMPORARY";
        string[] names = [absent, empty, nonempty, temporary];
        using var receipt = RunCase(sandbox, names, $$"""
            [Environment]::SetEnvironmentVariable('{{absent}}', [System.Management.Automation.Language.NullString]::Value, 'Process')
            [Environment]::SetEnvironmentVariable('{{temporary}}', [System.Management.Automation.Language.NullString]::Value, 'Process')
            [Environment]::SetEnvironmentVariable('{{empty}}', '', 'Process')
            [Environment]::SetEnvironmentVariable('{{nonempty}}', ' original=public ', 'Process')
            $seeded = Read-PublicEnvironment $names
            $snapshot = Get-MtpEnvironmentSnapshot
            $captured = @{ emptyPresent = $snapshot.ContainsKey('{{empty}}'); emptyValue = $snapshot['{{empty}}']; absentPresent = $snapshot.ContainsKey('{{absent}}') }
            foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, 'modified-public', 'Process') }
            $modified = Read-PublicEnvironment $names
            Restore-MtpEnvironment -Snapshot $snapshot
            @{ seeded = $seeded; captured = $captured; modified = $modified; local = (Read-PublicEnvironment $names); child = (Read-ChildEnvironment) }
            """);
        var seeded = receipt.RootElement.GetProperty("seeded");
        AssertState(seeded, absent, null);
        AssertState(seeded, temporary, null);
        AssertState(seeded, empty, "");
        AssertState(seeded, nonempty, " original=public ");
        var captured = receipt.RootElement.GetProperty("captured");
        Assert.True(captured.GetProperty("emptyPresent").GetBoolean(), "Snapshot lost public empty key membership.");
        Assert.Equal("", captured.GetProperty("emptyValue").GetString());
        Assert.False(captured.GetProperty("absentPresent").GetBoolean());
        foreach (var name in names)
            AssertState(receipt.RootElement.GetProperty("modified"), name, "modified-public");
        foreach (var observation in Observations(receipt))
        {
            AssertState(observation, absent, null);
            AssertState(observation, temporary, null);
            AssertState(observation, empty, "");
            AssertState(observation, nonempty, " original=public ");
        }
    }

    [Fact]
    public void Scrub_PublicGitIdentity_UsesExplicitConfiguration()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        string[] names = ["GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL", "EMAIL"];
        using var receipt = RunCase(sandbox, names, """
            $git = @(Get-Command git -CommandType Application -ErrorAction Stop)[0].Source
            foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, 'injected-public@example.invalid', 'Process') }
            $seeded = Read-PublicEnvironment $names
            Set-MtpHermeticEnvironment -RepositoryRoot $root -WritableRoot $root
            $local = Read-PublicEnvironment $names
            $child = Read-ChildEnvironment
            $identity = Invoke-OwnedChild -Executable $git -ChildArguments @('-c', 'user.name=BridgeFixture', '-c', 'user.email=fixture@example.invalid', 'var', 'GIT_AUTHOR_IDENT')
            @{ seeded = $seeded; local = $local; child = $child; gitExit = $identity.exitCode; identity = $identity.stdout.Trim() }
            """);
        foreach (var name in names)
        {
            AssertState(receipt.RootElement.GetProperty("seeded"), name, "injected-public@example.invalid");
            foreach (var observation in Observations(receipt))
                AssertState(observation, name, null);
        }
        Assert.Equal(0, receipt.RootElement.GetProperty("gitExit").GetInt32());
        Assert.Matches(@"^BridgeFixture <fixture@example\.invalid> \d+ [+-]\d{4}$",
            receipt.RootElement.GetProperty("identity").GetString()!);
    }

    [Fact]
    public void OwnedChild_DirectoryLaunch_ReportsBoundedFailureContext()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var directory = Directory.CreateDirectory(Path.Combine(sandbox.Root, "unstartable")).FullName;
        using var receipt = RunCase(sandbox, [], $$"""
            $failureMessage = $null
            $originalMessage = $null
            try { $null = Invoke-OwnedChild -Executable '{{Quote(directory)}}' -ChildArguments @() }
            catch {
                $failureMessage = $_.Exception.Message
                $originalMessage = $_.Exception.InnerException.Message
            }
            if ($null -eq $failureMessage) { throw 'Directory launch unexpectedly succeeded.' }
            $longMessage = $null
            try { $null = Invoke-OwnedChild -Executable ([IO.Path]::Combine($root, ('x' * 3000))) -ChildArguments @() }
            catch { $longMessage = $_.Exception.Message }
            if ($null -eq $longMessage) { throw 'Oversized executable launch unexpectedly succeeded.' }
            @{ local = (Read-PublicEnvironment @()); child = (Read-ChildEnvironment); failure = $failureMessage; original = $originalMessage; oversized = $longMessage }
            """);
        var message = receipt.RootElement.GetProperty("failure").GetString()!;
        Assert.StartsWith("OwnedChildLaunchFailure; ", message);
        Assert.InRange(message.Length, 1, 2000);
        Assert.Contains("executable=" + directory + ";", message);
        Assert.Contains("resolved=" + directory + ";", message);
        Assert.Contains("exists=True;", message);
        Assert.Contains("workingDirectory=" + sandbox.Root + ";", message);
        if (OperatingSystem.IsWindows())
            Assert.Matches(@"integrity=\w+ \(S-1-16-\d+\);", message);
        else
            Assert.Contains("integrity=unknown: non-Windows;", message);
        Assert.Contains("exceptionType=System.ComponentModel.Win32Exception;", message);
        Assert.Matches(@"nativeErrorCode=\d+;", message);
        var original = receipt.RootElement.GetProperty("original").GetString()!;
        Assert.NotEmpty(original);
        Assert.Contains("Exception calling", original);
        var boundedOriginal = original.Length <= 500 ? original : original[..486] + "...[truncated]";
        Assert.EndsWith("message=" + boundedOriginal, message);

        var oversized = receipt.RootElement.GetProperty("oversized").GetString()!;
        Assert.StartsWith("OwnedChildLaunchFailure; ", oversized);
        Assert.InRange(oversized.Length, 1, 2000);
        Assert.Contains("...[truncated]", oversized);
        foreach (var label in new[] { "executable=", "resolved=", "exists=", "workingDirectory=", "integrity=", "exceptionType=", "nativeErrorCode=", "message=" })
            Assert.Contains(label, oversized);
    }

    [Fact]
    public void OwnedChild_PwshLaunch_PreservesOutputAndExitCode()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        using var receipt = RunCase(sandbox, [], """
            $run = Invoke-OwnedChild -Executable $pwsh -ChildArguments @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', '[Console]::Out.Write("owned-child-control")')
            @{ local = (Read-PublicEnvironment @()); child = (Read-ChildEnvironment); run = $run }
            """);
        var run = receipt.RootElement.GetProperty("run");
        Assert.Equal(0, run.GetProperty("exitCode").GetInt32());
        Assert.Equal("owned-child-control", run.GetProperty("stdout").GetString());
        Assert.Equal("", run.GetProperty("stderr").GetString());
    }

    private JsonDocument RunCase(MtpTestRunnerScriptTests.ScriptSandbox sandbox, string[] names, string body)
    {
        var nameArguments = "@(" + string.Join(",", names.Select(name => $"'{Quote(name)}'")) + ")";
        var probe = Path.Combine(sandbox.Root, "observe-environment.ps1");
        File.WriteAllText(probe, ObservationFunction + $"\nRead-PublicEnvironment {nameArguments} | ConvertTo-Json -Depth 6 -Compress\n");
        var script = Path.Combine(sandbox.Root, "environment-case.ps1");
        var module = Path.Combine(sandbox.Root, "scripts", "MtpTestRunner.psm1");
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'Stop'
            try {
                Import-Module '{{Quote(module)}}' -Force -WarningAction SilentlyContinue
                & (Get-Module MtpTestRunner) {
                    $root = '{{Quote(sandbox.Root)}}'
                    $pwsh = [Environment]::ProcessPath
                    $probe = '{{Quote(probe)}}'
                    $names = {{nameArguments}}
                    {{ObservationFunction}}
                    {{OwnedChildFunctions}}
                    if ($PSVersionTable.PSVersion.Major -lt 7 -or [Environment]::Version.Major -lt 10) {
                        throw 'Environment-state regression requires modern pwsh on .NET 10 or newer.'
                    }
                    & { {{body}} } | ConvertTo-Json -Depth 8 -Compress
                }
                exit 0
            } catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
            """);
        var start = new ProcessStartInfo
        {
            FileName = ResolvePwsh(),
            WorkingDirectory = sandbox.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Pin the launch context; observation children inherit the resulting map without overrides.
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
                start.Environment[name] = value;
        }
        start.Environment["LOCALAPPDATA"] = sandbox.LocalApplicationDataRoot;
        foreach (var name in new[] { "TEMP", "TMP", "TMPDIR" })
            start.Environment[name] = sandbox.Root;
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
            start.ArgumentList.Add(argument);
        var result = MtpTestRunnerScriptTests.Run(start, timeout: TimeSpan.FromMinutes(5));
        Assert.True(result.ExitCode == 0, $"Environment case exited {result.ExitCode}: {Bounded(result.Stdout + result.Stderr)}");
        var receipt = JsonDocument.Parse(result.Stdout);
        foreach (var observation in Observations(receipt))
        {
            var pwsh = observation.GetProperty("pwsh").GetString()!;
            var dotnet = observation.GetProperty("dotnet").GetString()!;
            output.WriteLine($"Environment-state runtime: pwsh={pwsh}; .NET={dotnet}");
            Assert.True(Version.Parse(pwsh.Split('-')[0]).Major >= 7, "Probe did not execute modern pwsh.");
            Assert.True(Version.Parse(dotnet).Major >= 10, "Probe did not execute the supported modern .NET runtime.");
        }
        return receipt;
    }

    private static IEnumerable<JsonElement> Observations(JsonDocument receipt)
    {
        yield return receipt.RootElement.GetProperty("local");
        yield return receipt.RootElement.GetProperty("child");
    }

    private static void AssertState(JsonElement observation, string name, string? expected)
    {
        var state = observation.GetProperty("states").GetProperty(name);
        Assert.True(state.GetProperty("present").GetBoolean() == (expected is not null),
            $"Public sentinel {name}: expected {(expected is null ? "absent" : "present")} membership.");
        Assert.Equal(expected, state.GetProperty("value").GetString());
    }

    private static string ResolvePwsh()
    {
        // Resolve once before scrubbing; never fall back to Windows PowerShell.
        var leaf = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory.Trim('"'), leaf);
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                return candidate;
        }
        throw new InvalidOperationException("Environment-state regression requires pwsh on PATH; no Windows PowerShell fallback is permitted.");
    }

    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static string Bounded(string value) => value.Length <= 4000 ? value : value[..4000];

    private const string ObservationFunction = """
        function Read-PublicEnvironment {
            param([string[]]$Names)
            $map = [Environment]::GetEnvironmentVariables([EnvironmentVariableTarget]::Process)
            $states = [ordered]@{}
            foreach ($name in $Names) {
                $present = $map.Contains($name)
                $value = if ($present) { [string]$map[$name] } else { $null }
                $states[$name] = @{ present = $present; value = $value }
            }
            @{ pwsh = $PSVersionTable.PSVersion.ToString(); dotnet = [Environment]::Version.ToString(); states = $states }
        }
        """;

    private const string OwnedChildFunctions = """
        function Invoke-OwnedChild {
            param([string]$Executable, [string[]]$ChildArguments)
            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = $Executable
            $start.WorkingDirectory = $root
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            foreach ($argument in $ChildArguments) { $start.ArgumentList.Add($argument) }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $start
            $started = $false
            try {
                try { $started = $process.Start() }
                catch {
                    $launchException = $_.Exception
                    $exception = $launchException
                    while (($exception -is [System.Management.Automation.MethodInvocationException] -or $exception -is [Reflection.TargetInvocationException]) -and $null -ne $exception.InnerException) {
                        $exception = $exception.InnerException
                    }
                    $nativeErrorCode = 'unavailable'
                    for ($errorCause = $launchException; $null -ne $errorCause; $errorCause = $errorCause.InnerException) {
                        if ($errorCause -is [ComponentModel.Win32Exception]) { $nativeErrorCode = [string]$errorCause.NativeErrorCode; break }
                    }
                    $workingDirectory = 'unknown'
                    try {
                        $workingDirectory = if ([string]::IsNullOrEmpty($start.WorkingDirectory)) { [Environment]::CurrentDirectory } else { $start.WorkingDirectory }
                    } catch { $workingDirectory = 'unknown: ' + $_.Exception.Message }
                    $resolved = 'unresolved'
                    $exists = 'unknown'
                    try {
                        # Native launch lookup uses the parent's current directory, not PowerShell's location or command table.
                        $currentDirectory = [Environment]::CurrentDirectory
                        if ([IO.Path]::IsPathRooted($Executable) -or $Executable.Contains('/') -or $Executable.Contains('\')) {
                            $resolved = [IO.Path]::GetFullPath($Executable, $currentDirectory)
                        } else {
                            $directories = @()
                            if ($IsWindows) {
                                $windowsDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
                                $directories += [IO.Path]::GetDirectoryName([Environment]::ProcessPath), $currentDirectory, [Environment]::SystemDirectory, ([IO.Path]::Combine($windowsDirectory, 'System')), $windowsDirectory
                            }
                            $directories += ([string]$start.Environment['PATH']).Split([IO.Path]::PathSeparator)
                            foreach ($directory in $directories) {
                                $names = @($Executable)
                                if ($IsWindows -and [string]::IsNullOrEmpty([IO.Path]::GetExtension($Executable))) { $names += $Executable + '.exe' }
                                foreach ($name in $names) {
                                    $candidate = [IO.Path]::GetFullPath([IO.Path]::Combine($directory.Trim('"'), $name), $currentDirectory)
                                    if ([IO.File]::Exists($candidate) -or [IO.Directory]::Exists($candidate)) { $resolved = $candidate; break }
                                }
                                if ($resolved -ne 'unresolved') { break }
                            }
                        }
                        if ($resolved -ne 'unresolved') { $exists = [string]([IO.File]::Exists($resolved) -or [IO.Directory]::Exists($resolved)) }
                    } catch { $resolved = 'unresolved'; $exists = 'unknown' }
                    $integrity = 'unknown: non-Windows'
                    if ($IsWindows) {
                        try {
                            if (-not ('OwnedChildLaunchNative' -as [type])) {
                                Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class OwnedChildLaunchNative { [DllImport("advapi32.dll", SetLastError=true)] public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token); [DllImport("advapi32.dll", SetLastError=true)] public static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr buffer, uint size, out uint needed); [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle); }' -ErrorAction Stop
                            }
                            $token = [IntPtr]::Zero
                            $buffer = [IntPtr]::Zero
                            try {
                                if (-not [OwnedChildLaunchNative]::OpenProcessToken([IntPtr]::new(-1), 8, [ref]$token)) { throw [ComponentModel.Win32Exception]::new() }
                                [uint32]$needed = 0
                                [void][OwnedChildLaunchNative]::GetTokenInformation($token, 25, [IntPtr]::Zero, 0, [ref]$needed)
                                if ($needed -eq 0) { throw [ComponentModel.Win32Exception]::new() }
                                $buffer = [Runtime.InteropServices.Marshal]::AllocHGlobal([int]$needed)
                                if (-not [OwnedChildLaunchNative]::GetTokenInformation($token, 25, $buffer, $needed, [ref]$needed)) { throw [ComponentModel.Win32Exception]::new() }
                                $sid = [Security.Principal.SecurityIdentifier]::new([Runtime.InteropServices.Marshal]::ReadIntPtr($buffer))
                                $rid = [int]$sid.Value.Split('-')[-1]
                                $level = if ($rid -lt 4096) { 'Untrusted' } elseif ($rid -lt 8192) { 'Low' } elseif ($rid -lt 8448) { 'Medium' } elseif ($rid -lt 12288) { 'MediumPlus' } elseif ($rid -lt 16384) { 'High' } elseif ($rid -lt 20480) { 'System' } else { 'Protected' }
                                $integrity = $level + ' (' + $sid.Value + ')'
                            } finally {
                                if ($buffer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::FreeHGlobal($buffer) }
                                if ($token -ne [IntPtr]::Zero) { [void][OwnedChildLaunchNative]::CloseHandle($token) }
                            }
                        } catch { $integrity = 'unknown: ' + $_.Exception.Message }
                    }
                    $fields = [ordered]@{ executable = $Executable; resolved = $resolved; exists = $exists; workingDirectory = $workingDirectory; integrity = $integrity; exceptionType = $exception.GetType().FullName; nativeErrorCode = $nativeErrorCode; message = $launchException.Message }
                    $limits = @{ executable = 300; resolved = 300; exists = 32; workingDirectory = 300; integrity = 120; exceptionType = 120; nativeErrorCode = 32; message = 500 }
                    $parts = foreach ($field in $fields.GetEnumerator()) {
                        $value = [string]$field.Value
                        $limit = $limits[$field.Key]
                        $suffix = '...[truncated]'
                        if ($value.Length -gt $limit) { $value = $value.Substring(0, $limit - $suffix.Length) + $suffix }
                        $field.Key + '=' + $value
                    }
                    # The field budgets total less than 2000 including the marker, labels and separators.
                    $diagnostic = 'OwnedChildLaunchFailure; ' + ($parts -join '; ')
                    throw [InvalidOperationException]::new($diagnostic, $launchException)
                }
                if (-not $started) { throw 'Owned environment probe did not start.' }
                $stdout = $process.StandardOutput.ReadToEndAsync()
                $stderr = $process.StandardError.ReadToEndAsync()
                # Hang guard only: the child must complete, with no elapsed-time assertion.
                if (-not $process.WaitForExit(120000)) { throw 'Owned environment probe did not exit before the hang guard.' }
                @{ exitCode = $process.ExitCode; stdout = $stdout.GetAwaiter().GetResult(); stderr = $stderr.GetAwaiter().GetResult() }
            } finally {
                if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(10000) }
                $process.Dispose()
            }
        }
        function Read-ChildEnvironment {
            $run = Invoke-OwnedChild -Executable $pwsh -ChildArguments @('-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $probe)
            if ($run.exitCode -ne 0) { throw 'Owned environment observation child failed.' }
            $run.stdout | ConvertFrom-Json -ErrorAction Stop
        }
        """;
}
