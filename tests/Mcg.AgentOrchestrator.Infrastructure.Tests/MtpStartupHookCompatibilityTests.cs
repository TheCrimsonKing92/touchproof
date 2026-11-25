using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

public sealed class MtpStartupHookCompatibilityTests
{
    private const string HookAssembly = "Mcg.AgentOrchestrator.FaultDialogStartupHook.dll";
    private const string Marker = ".mcg-startup-hook.json";
    private const string HostBoundHook = "host-bound-hook.dll";

    [Fact]
    public void SetChildEnvironmentSuppressesGlobalToolsPathPersistence()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows startup hook.");
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
        start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "1";

        SetChildEnvironment(start, sandbox);

        Assert.Equal("0", start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"]);
    }

    [Fact]
    public void PwshHookReferencesMscorlibOnlyOfTheTwoContracts()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows PowerShell compiler contract.");
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success", "startup hook's");

        var hook = ResolveFreshHook(sandbox);

        AssertFrameworkReferences(hook);
    }

    [Fact]
    public void FreshHookLoadsInNet10MsBuild()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows startup hook.");
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var hook = ResolveFreshHook(sandbox);
        var project = Path.Combine(sandbox.Root, "runtime.proj");
        File.WriteAllText(project, """
            <Project>
              <PropertyGroup>
                <MtpRuntimeVersion>$([System.Environment]::Version)</MtpRuntimeVersion>
              </PropertyGroup>
            </Project>
            """);
        var start = new ProcessStartInfo
        {
            FileName = ResolveDotnetHostPath(),
            WorkingDirectory = MtpTestRunnerScriptTests.RepositoryRoot(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "msbuild", project, "-nologo", "-noAutoResponse", "-getProperty:MtpRuntimeVersion" }
        };
        SetChildEnvironment(start, sandbox);
        start.Environment["DOTNET_CLI_HOME"] = sandbox.LocalApplicationDataRoot;
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_ROLL_FORWARD"] = "LatestPatch";
        start.Environment["DOTNET_STARTUP_HOOKS"] = hook;
        start.Environment["MSBUILDENABLEALLPROPERTYFUNCTIONS"] = "1";

        var result = MtpTestRunnerScriptTests.Run(start);

        AssertSuccessful(result);
        Assert.True(Version.TryParse(result.Stdout.Trim(), out var runtime), result.Stdout + result.Stderr);
        Assert.Equal(10, runtime.Major);
        AssertFrameworkReferences(hook);
    }

    [Fact]
    public void ResolverRebuildsDigestCorrectSchemaOneEntry()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows startup-hook cache.");
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var sourceDigest = CompileHostBoundHook(sandbox);
        var legacyDirectory = Path.Combine(CacheRoot(sandbox), "v1", sourceDigest);
        Directory.CreateDirectory(legacyDirectory);
        var legacyAssembly = Path.Combine(legacyDirectory, HookAssembly);
        File.Move(Path.Combine(sandbox.Root, HostBoundHook), legacyAssembly);
        var legacyDigest = DigestFile(legacyAssembly);
        var legacyMarker = JsonSerializer.Serialize(new { schemaVersion = 1, sourceDigest, assemblyDigest = legacyDigest });
        File.WriteAllText(Path.Combine(legacyDirectory, Marker), legacyMarker);

        var result = RunPwsh(sandbox, """
            $script:originalWriter = (Get-Command Write-MtpFaultDialogStartupHookAssembly).ScriptBlock
            $script:compilerCalls = 0
            function Write-MtpFaultDialogStartupHookAssembly {
                param($Destination)
                $script:compilerCalls++
                & $script:originalWriter -Destination $Destination
            }
            $hook = Resolve-MtpFaultDialogStartupHook
            @{ hook = $hook; compilerCalls = $script:compilerCalls } | ConvertTo-Json -Compress
            """);

        AssertSuccessful(result);
        using var compilation = JsonDocument.Parse(result.Stdout);
        Assert.Equal(1, compilation.RootElement.GetProperty("compilerCalls").GetInt32());
        var hook = compilation.RootElement.GetProperty("hook").GetString()!;
        var published = Path.Combine(CacheRoot(sandbox), "v2", sourceDigest);
        Assert.Equal(Path.Combine(published, HookAssembly), hook, ignoreCase: true);
        AssertFrameworkReferences(hook);
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(published, Marker)));
        Assert.Equal(2, marker.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("windows-powershell-framework-codedom", marker.RootElement.GetProperty("compilerKind").GetString());
        Assert.Equal(sourceDigest, marker.RootElement.GetProperty("sourceDigest").GetString());
        Assert.Equal(DigestFile(hook), marker.RootElement.GetProperty("assemblyDigest").GetString());
        Assert.Equal(legacyMarker, File.ReadAllText(Path.Combine(legacyDirectory, Marker)));
        Assert.Equal(legacyDigest, DigestFile(legacyAssembly));

        var reused = RunPwsh(sandbox, """
            function Write-MtpFaultDialogStartupHookAssembly { throw 'The schema-2 entry must be reused.' }
            Resolve-MtpFaultDialogStartupHook
            """);
        AssertSuccessful(reused);
        Assert.Equal(hook, reused.Stdout.Trim());
    }

    [Fact]
    public void PublishRejectsSystemRuntimeReference()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows startup-hook publication.");
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        CompileHostBoundHook(sandbox);
        var offendingReference = Assert.Single(ReadReferences(Path.Combine(sandbox.Root, HostBoundHook)),
            reference => reference.Name == "System.Runtime");

        var result = RunPwsh(sandbox, $$"""
            # This fixture exercises hook publication. The Low-integrity label has separate coverage.
            Set-Item -Path 'Function:script:Set-MtpLowIntegrityLabel' -Value { param($Path) }
            $script:incompatibleAssembly = '{{Quote(Path.Combine(sandbox.Root, HostBoundHook))}}'
            function Write-MtpFaultDialogStartupHookAssembly {
                param($Destination)
                [IO.File]::Copy($script:incompatibleAssembly, $Destination)
            }
            $root = '{{Quote(sandbox.Root)}}'
            $manifest = Read-MtpTestManifest (Join-Path $root 'config/acceptance-manifest.json')
            $run = Invoke-MtpTestRun -RepositoryRoot $root -Manifest $manifest `
                -Target 'tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj' `
                -RunLabel 'incompatible-hook' -NoBuild -RunnerPath '{{Quote(sandbox.RunnerPath)}}' `
                -ResultsRoot '{{Quote(sandbox.ResultsRoot)}}'
            exit $run.ExitCode
            """);

        Assert.True(result.ExitCode == 30, $"Exit {result.ExitCode}{Environment.NewLine}{result.Stdout}{result.Stderr}");
        Assert.Contains("STARTUP HOOK FAILURE - managed build/test processes were not launched", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(offendingReference.FullName, result.Stdout, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(CacheRoot(sandbox), "v2")));
        Assert.False(File.Exists(sandbox.ArgumentLog));
    }

    [Fact]
    public void DelegatedCompilerReceivesWindowsEnvironmentAndLeavesScratchClean()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows PowerShell compiler environment.");
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var scratch = Directory.CreateDirectory(Path.Combine(sandbox.Root, "compiler-caller")).FullName;
        var probe = Path.Combine(sandbox.Root, "compiler-environment.json");
        var module = Path.Combine(sandbox.Root, "scripts", "MtpTestRunner.psm1");
        File.AppendAllText(module, Environment.NewLine + $$"""
            $script:originalCodeDom = (Get-Command Invoke-MtpStartupHookCodeDom).ScriptBlock
            function Invoke-MtpStartupHookCodeDom {
                param($Source, $AssemblyPath)
                & $script:originalCodeDom -Source $Source -AssemblyPath $AssemblyPath
                $receipt = @{
                    version = $PSVersionTable.PSVersion.ToString()
                    systemDrive = $env:SystemDrive
                    systemRoot = $env:SystemRoot
                    windir = $env:WINDIR
                    workingDirectory = [Environment]::CurrentDirectory
                } | ConvertTo-Json -Compress
                [IO.File]::WriteAllText('{{Quote(probe)}}', $receipt)
            }
            """);

        var result = RunPwsh(sandbox, """
            $env:SystemDrive = $null
            $env:SystemRoot = $null
            $env:WINDIR = $null
            Resolve-MtpFaultDialogStartupHook
            """, scratch);

        AssertSuccessful(result);
        AssertFrameworkReferences(result.Stdout.Trim());
        Assert.True(File.Exists(probe), "The Windows PowerShell compiler did not write its environment receipt.");
        using var receipt = JsonDocument.Parse(File.ReadAllText(probe));
        var child = receipt.RootElement;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.StartsWith("5.1.", child.GetProperty("version").GetString(), StringComparison.Ordinal);
        Assert.Equal(Path.GetPathRoot(windows)!.TrimEnd('\\'), child.GetProperty("systemDrive").GetString(), ignoreCase: true);
        Assert.Equal(windows, child.GetProperty("systemRoot").GetString(), ignoreCase: true);
        Assert.Equal(windows, child.GetProperty("windir").GetString(), ignoreCase: true);
        var compilerDirectory = child.GetProperty("workingDirectory").GetString()!;
        Assert.Equal(Path.Combine(sandbox.LocalApplicationDataRoot, "Temp", "mcg-mtp-compile"),
            Path.GetDirectoryName(compilerDirectory), ignoreCase: true);
        Assert.False(Directory.Exists(compilerDirectory), $"Compiler working directory still exists: {compilerDirectory}");
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
    }

    private static string ResolveFreshHook(MtpTestRunnerScriptTests.ScriptSandbox sandbox)
    {
        var result = RunPwsh(sandbox, "Resolve-MtpFaultDialogStartupHook");
        AssertSuccessful(result);
        var hook = result.Stdout.Trim();
        Assert.True(File.Exists(hook), $"The resolver did not return a compiled hook. {result.Stdout}{result.Stderr}");
        return hook;
    }

    private static string CompileHostBoundHook(MtpTestRunnerScriptTests.ScriptSandbox sandbox)
    {
        var result = RunPwsh(sandbox, $$"""
            Add-Type -TypeDefinition $script:MtpStartupHookSource -OutputAssembly '{{Quote(Path.Combine(sandbox.Root, HostBoundHook))}}' -OutputType Library
            [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($script:MtpStartupHookSource))
            """);
        AssertSuccessful(result);
        return Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(result.Stdout.Trim())));
    }

    private static MtpTestRunnerScriptTests.ProcessResult RunPwsh(
        MtpTestRunnerScriptTests.ScriptSandbox sandbox, string body, string? workingDirectory = null)
    {
        var start = sandbox.SandboxPowerShellStartInfo();
        start.FileName = TestPowerShell.Executable;
        // A child using the sandbox as its working directory can prevent Windows from deleting it.
        start.WorkingDirectory = workingDirectory ?? MtpTestRunnerScriptTests.RepositoryRoot();
        SetChildEnvironment(start, sandbox);
        var module = Quote(Path.Combine(sandbox.Root, "scripts", "MtpTestRunner.psm1"));
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($$"""
            $ErrorActionPreference = 'Stop'
            if ($PSVersionTable.PSEdition -ne 'Core') { throw 'This test requires pwsh.' }
            $module = Import-Module '{{module}}' -Force -PassThru
            & $module {
                {{body}}
            }
            """);
        return MtpTestRunnerScriptTests.Run(start);
    }

    private static void SetChildEnvironment(ProcessStartInfo start, MtpTestRunnerScriptTests.ScriptSandbox sandbox)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var temp = Directory.CreateDirectory(Path.Combine(sandbox.Root, "temp")).FullName;
        start.Environment.Clear();
        // Keep the SDK from persisting the sandbox tools directory in the user PATH.
        start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "0";
        start.Environment["SystemDrive"] = Path.GetPathRoot(windows)!.TrimEnd('\\');
        start.Environment["SystemRoot"] = windows;
        start.Environment["WINDIR"] = windows;
        start.Environment["ComSpec"] = Path.Combine(windows, "System32", "cmd.exe");
        start.Environment["PATH"] = Path.Combine(windows, "System32");
        start.Environment["TEMP"] = temp;
        start.Environment["TMP"] = temp;
        start.Environment["LOCALAPPDATA"] = sandbox.LocalApplicationDataRoot;
        start.Environment["USERPROFILE"] = sandbox.LocalApplicationDataRoot;
    }

    private static string CacheRoot(MtpTestRunnerScriptTests.ScriptSandbox sandbox) =>
        Path.Combine(sandbox.Root, "temp", "mcg-mtp-startup-hook");

    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string DigestFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void AssertSuccessful(MtpTestRunnerScriptTests.ProcessResult result) =>
        Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode}{Environment.NewLine}{result.Stdout}{result.Stderr}");

    private static void AssertFrameworkReferences(string path)
    {
        var references = ReadReferences(path);
        Assert.Contains(references, reference => reference.Name == "mscorlib");
        Assert.DoesNotContain(references, reference => reference.Name == "System.Runtime");
    }

    private static AssemblyName[] ReadReferences(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences.Select(handle => metadata.GetAssemblyReference(handle).GetAssemblyName()).ToArray();
    }
}
