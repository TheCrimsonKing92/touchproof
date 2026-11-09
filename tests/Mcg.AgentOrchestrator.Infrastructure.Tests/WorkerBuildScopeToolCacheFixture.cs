using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Exercise the real helper/cache scripts with a tiny home and a recorded dotnet
// command. No scope-tool compilation or candidate project closure is needed.
internal sealed class WorkerBuildScopeToolCacheFixture : IDisposable
{
    internal string Parent { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "sc-" + Guid.NewGuid().ToString("N"));
    internal string Candidate { get; } = VerifiedRepositoryRoot.Find();
    internal string Cache => Path.Combine(Parent, "cache");
    internal string Home => Path.Combine(Parent, "home");
    internal string Stub => Path.Combine(Parent, "dotnet.ps1");
    private static readonly string GitPath = ResolveGitPath();

    private static string ResolveGitPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!OperatingSystem.IsWindows()) return path;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Git for Windows' cmd/git.exe launches mingw64/bin/git.exe. Keep the
            // same installation and real Git semantics without a launcher per probe.
            var launcher = Path.Combine(directory.Trim('"'), "git.exe");
            if (!File.Exists(launcher)) continue;
            var native = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(launcher)!, "..", "mingw64", "bin", "git.exe"));
            return File.Exists(native) ? Path.GetDirectoryName(native) + Path.PathSeparator + path : path;
        }
        return path;
    }

    internal WorkerBuildScopeToolCacheFixture()
    {
        foreach (var relative in new[]
        {
            "scripts/Invoke-WorkerBuildCheck.ps1", "scripts/WorkerBuildScopeToolCache.ps1", "scripts/WorkerBuildReceipt.psm1",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager.cs"
        })
        {
            var destination = Path.Combine(Home, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(Candidate, relative), destination);
        }
        WriteHome("scripts/WorkerBuildScopeTool/WorkerBuildScopeTool.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../../src/Mcg.AgentOrchestrator.Execution/Mcg.AgentOrchestrator.Execution.csproj" /></ItemGroup></Project>
            """);
        WriteHome("scripts/WorkerBuildScopeTool/Program.cs", "// Tiny scope tool fixture; dotnet is replaced at the process boundary.");
        WriteHome("src/Mcg.AgentOrchestrator.Execution/Mcg.AgentOrchestrator.Execution.csproj", """
            <Project><ItemGroup><ProjectReference Include="../Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj" /><ProjectReference Include="../Mcg.AgentOrchestrator.Infrastructure.Providers/Mcg.AgentOrchestrator.Infrastructure.Providers.csproj" /></ItemGroup></Project>
            """);
        WriteHome("src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj", "<Project />");
        WriteHome("src/Mcg.AgentOrchestrator.Infrastructure.Providers/Mcg.AgentOrchestrator.Infrastructure.Providers.csproj", "<Project />");
        WriteHome("Directory.Build.props", "<Project />");
        WriteHome("Directory.Build.targets", "<Project />");
        WriteHome("Directory.Build.rsp", "-nodeReuse:false");
        WriteHome("global.json", "{}");
        WriteHome(".gitignore", "**/bin/\n**/obj/\n");
        InitGit(Home);
        File.WriteAllText(Stub, """
            # Replace only the external command. Both production scripts and their
            # git, validation, publication and receipt logic execute unchanged.
            function global:dotnet {
                $values = [string[]]@($args)
                $global:LASTEXITCODE = 0
                if ($values.Count -eq 1 -and $values[0] -eq '--version') { '10.0.100-fixture'; return }
                [IO.File]::AppendAllText($env:CACHE_VECTOR_LOG, (ConvertTo-Json -InputObject $values -Compress) + "`n")
                if ($values[0] -eq 'build' -and $values[1].EndsWith('WorkerBuildScopeTool.csproj')) {
                    if ($env:CACHE_BUILD_MODE -eq 'fail') { $global:LASTEXITCODE = 9; return }
                    if ($env:CACHE_BUILD_MODE -eq 'barrier') {
                        $ready = [Threading.EventWaitHandle]::OpenExisting($env:CACHE_READY)
                        $release = [Threading.EventWaitHandle]::OpenExisting($env:CACHE_RELEASE)
                        try {
                            [void]$ready.Set()
                            if (-not $release.WaitOne(300000)) { throw 'Cache concurrent-build release event was not signaled.' }
                        }
                        finally { $ready.Dispose(); $release.Dispose() }
                    }
                    elseif ($env:CACHE_BUILD_MODE -ne 'stub') { throw 'Unknown scope build fixture mode.' }
                    $index = [Array]::IndexOf($values, '--output')
                    if ($index -lt 0) { throw 'Scope build omitted --output.' }
                    $output = $values[$index + 1]
                    [void][IO.Directory]::CreateDirectory($output)
                    [IO.File]::WriteAllText((Join-Path $output 'WorkerBuildScopeTool.dll'), 'fixture scope-tool output v1')
                    [IO.File]::WriteAllText((Join-Path $output 'WorkerBuildScopeTool.runtimeconfig.json'), '{}')
                    [IO.File]::WriteAllText((Join-Path $output 'WorkerBuildScopeTool.deps.json'), '{}')
                    return
                }
                if ([IO.Path]::GetFileName($values[0]) -eq 'WorkerBuildScopeTool.dll') {
                    if ([IO.File]::ReadAllText($values[0]) -cne 'fixture scope-tool output v1') { throw 'Executed corrupt scope output.' }
                    if ($values.Count -ne 4 -or [IO.Path]::GetFileName($values[3]) -ne 'One.csproj') { throw 'Unexpected fixture scope arguments.' }
                    @{ Projects = @('One.csproj'); RequestedProjects = @('One.csproj'); RequiredProjects = @('One.csproj')
                       MetadataDigest = 'none'; StableMetadataDigest = 'none'; Identity = 'fixture-scope-v1' } |
                        ConvertTo-Json | Set-Content -LiteralPath $values[2] -Encoding UTF8
                    return
                }
                if ($values[0] -ne 'build') { throw "Unexpected fixture dotnet command: $values" }
                $logger = @($values | Where-Object { $_.StartsWith('-flp:LogFile=') })
                if ($logger.Count -ne 1) { throw 'Requested build omitted file logger.' }
                $path = $logger[0].Substring('-flp:LogFile='.Length).Split(';')[0]
                [IO.File]::WriteAllText($path, 'Build succeeded.')
            }
            """);
        File.WriteAllText(Path.Combine(Parent, "helper.ps1"), """
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            . $env:CACHE_STUB
            & (Join-Path $env:CACHE_HOME 'scripts/Invoke-WorkerBuildCheck.ps1') $env:CACHE_PROJECT
            exit $LASTEXITCODE
            """);
    }

    private void WriteHome(string relative, string content)
    {
        var path = Path.Combine(Home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    internal ProcessStartInfo Shell(string cwd, params string[] arguments)
    {
        var info = new ProcessStartInfo(WorkerShell.Executable)
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Pin the complete child environment; worker channels and acceptance flags
        // cannot bypass the path this fixture claims to exercise.
        info.Environment.Clear();
        foreach (var name in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "DOTNET_ROOT", "DOTNET_ROOT_X64", "NUGET_PACKAGES", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        info.Environment["PATH"] = GitPath;
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass" }.Concat(arguments)) info.ArgumentList.Add(argument);
        return info;
    }

    internal ProcessStartInfo CacheShell(string cwd, string mode, string log, params string[] arguments)
    {
        var info = Shell(cwd, arguments);
        info.Environment["CACHE_HOME"] = Home;
        info.Environment["CACHE_STUB"] = Stub;
        info.Environment["CACHE_VECTOR_LOG"] = log;
        info.Environment["CACHE_BUILD_MODE"] = mode;
        info.Environment["MCG_WORKER_BUILD_SCOPE_TOOL_CACHE"] = Cache;
        return info;
    }

    internal string Work(string name)
    {
        var root = Path.Combine(Parent, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "One.csproj"), "<Project />");
        InitGit(root);
        return root;
    }

    internal ProcessStartInfo Helper(string work, string mode, string log)
    {
        var info = CacheShell(work, mode, log, "-File", Path.Combine(Parent, "helper.ps1"));
        info.Environment["CACHE_PROJECT"] = Path.Combine(work, "One.csproj");
        info.Environment["MCG_DOTNET_ISOLATED_ROOT"] = work + "-outputs";
        return info;
    }

    internal TestChildProcessCapture.Result[] RepeatedHelpers(string work, string log)
    {
        var driver = Path.Combine(Parent, "repeated.ps1");
        File.WriteAllText(driver, """
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            . $env:CACHE_STUB
            $originalTemp = $env:TEMP
            $originalTmp = $env:TMP
            $vectorLog = $env:CACHE_VECTOR_LOG
            $runs = @(
                for ($index = 0; $index -lt 4; $index++) {
                    # The real helper is called in its own script scope (including
                    # its exit statement). Restore its process environment between
                    # calls so a deleted process-temp folder cannot become TEMP.
                    $env:TEMP = $originalTemp
                    $env:TMP = $originalTmp
                    $env:CACHE_VECTOR_LOG = "$vectorLog.$index"
                    if ($index -eq 2) {
                        $entries = [IO.Directory]::GetDirectories($env:MCG_WORKER_BUILD_SCOPE_TOOL_CACHE)
                        if ($entries.Count -ne 1) { throw 'Expected one entry before corrupting its DLL.' }
                        $dll = Join-Path $entries[0] 'WorkerBuildScopeTool.dll'
                        $bytes = [IO.File]::ReadAllBytes($dll)
                        $bytes[0] = $bytes[0] -bxor 255
                        [IO.File]::WriteAllBytes($dll, $bytes)
                    }
                    $lines = @(& (Join-Path $env:CACHE_HOME 'scripts/Invoke-WorkerBuildCheck.ps1') $env:CACHE_PROJECT)
                    $code = $LASTEXITCODE
                    $entries = [IO.Directory]::GetDirectories($env:MCG_WORKER_BUILD_SCOPE_TOOL_CACHE)
                    if ($entries.Count -ne 1) { throw 'Expected one published cache entry.' }
                    [pscustomobject]@{
                        ExitCode = $code; Stdout = ($lines -join "`n")
                        Entry = $entries[0]
                        Manifest = [IO.File]::ReadAllText((Join-Path $entries[0] 'manifest.json'))
                        DllHash = (Get-FileHash -LiteralPath (Join-Path $entries[0] 'WorkerBuildScopeTool.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
                    }
                }
            )
            ConvertTo-Json -InputObject $runs -Depth 4 -Compress
            exit 0
            """);
        var start = Helper(work, "stub", log);
        start.ArgumentList[^1] = driver;
        var captured = Run(start);
        Assert.True(captured.ExitCode == 0, captured.Stdout + captured.Stderr);
        using var document = JsonDocument.Parse(captured.Stdout);
        var rows = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(4, rows.Length);
        return rows.Select(row =>
        {
            // Validate each generation's snapshot, including the first build and
            // repair, rather than inspecting only the final filesystem state.
            using var manifest = JsonDocument.Parse(row.GetProperty("Manifest").GetString()!);
            Assert.Equal(Path.GetFileName(row.GetProperty("Entry").GetString()), manifest.RootElement.GetProperty("sourceDigest").GetString());
            Assert.Equal(row.GetProperty("DllHash").GetString(), manifest.RootElement.GetProperty("dllSha256").GetString());
            return new TestChildProcessCapture.Result(row.GetProperty("ExitCode").GetInt32(), row.GetProperty("Stdout").GetString()!, captured.Stderr);
        }).ToArray();
    }

    internal void InitGit(string root)
    {
        Git(root, "init", "--initial-branch=fixture");
        Git(root, "config", "core.autocrlf", "false");
        Git(root, "add", "-A");
        Git(root, "-c", "user.name=Cache Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=", "commit", "-m", "fixture");
    }

    internal string Git(string root, params string[] arguments)
    {
        var environment = new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.OrdinalIgnoreCase);
        environment["PATH"] = GitPath;
        var result = InfrastructureTestSupport.RunGitProbe(root, arguments, inheritedEnvironment: environment);
        Assert.True(result.Succeeded, result.ToString());
        InfrastructureTestSupport.RequireCompleteGitOutput(result);
        return result.StandardOutput;
    }

    internal static TestChildProcessCapture.Result Run(ProcessStartInfo info) =>
        TestChildProcessCapture.Run(info, hangBound: TimeSpan.FromMinutes(10), start: Start);

    internal static Process? Start(ProcessStartInfo info)
    {
        var process = Process.Start(info);
        process?.StandardInput.Close();
        return process;
    }

    internal static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

    internal static void ValidEntry(string entry)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(entry, "manifest.json")));
        Assert.Equal(Path.GetFileName(entry), manifest.RootElement.GetProperty("sourceDigest").GetString());
        Assert.Equal(Hash(Path.Combine(entry, "WorkerBuildScopeTool.dll")), manifest.RootElement.GetProperty("dllSha256").GetString());
        Assert.True(File.Exists(Path.Combine(entry, "WorkerBuildScopeTool.runtimeconfig.json")));
        Assert.True(File.Exists(Path.Combine(entry, "WorkerBuildScopeTool.deps.json")));
    }

    public void Dispose() => ScriptSandboxCleanup.DeleteOrThrow([Parent]);
}
