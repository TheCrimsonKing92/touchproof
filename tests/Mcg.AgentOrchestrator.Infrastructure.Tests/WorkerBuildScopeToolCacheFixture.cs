using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// All processes, caches, logs and event names belong to this fixture. The only
// real cold builds are the seed and corrupt-entry repair in the integration test.
internal sealed class WorkerBuildScopeToolCacheFixture : IDisposable
{
    internal string Parent { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "sc-" + Guid.NewGuid().ToString("N"));
    internal string Candidate { get; } = VerifiedRepositoryRoot.Find();
    internal string Cache => Path.Combine(Parent, "cache");
    internal string Shim => Path.Combine(Parent, "shim");
    private readonly string _dotnet;

    internal WorkerBuildScopeToolCacheFixture()
    {
        Directory.CreateDirectory(Shim);
        var lookup = Run(Shell(Candidate, "-Command", "@(Get-Command dotnet -CommandType Application)[0].Source"));
        Assert.True(lookup.ExitCode == 0, lookup.Stdout + lookup.Stderr);
        _dotnet = lookup.Stdout.Trim();
        Assert.True(File.Exists(_dotnet), "Real dotnet executable was not resolved: " + _dotnet);
        File.WriteAllText(Path.Combine(Shim, "dotnet.cmd"), """
            @echo off
            set "CACHE_RAW=%*"
            if "%~1"=="--version" goto real
            if not "%CACHE_RAW:WorkerBuildScopeTool.dll=%"=="%CACHE_RAW%" goto real
            if not "%CACHE_RAW:WorkerBuildScopeTool.csproj=%"=="%CACHE_RAW%" if "%CACHE_BUILD_MODE%"=="real" goto buildreal
            "%CACHE_SHELL%" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0shim.ps1"
            exit /b %ERRORLEVEL%
            :buildreal
            "%CACHE_SHELL%" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0shim.ps1" -LogOnly
            if errorlevel 1 exit /b %ERRORLEVEL%
            :real
            "%CACHE_DOTNET%" %*
            exit /b %ERRORLEVEL%
            """);
        File.WriteAllText(Path.Combine(Shim, "shim.ps1"), """
            param([switch]$LogOnly)
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            $raw = $env:CACHE_RAW
            Add-Content -LiteralPath $env:CACHE_VECTOR_LOG -Value $raw
            if ($LogOnly) { exit 0 }
            if ($raw -like '*WorkerBuildScopeTool.csproj*') {
                if ($env:CACHE_BUILD_MODE -eq 'fail') { exit 9 }
                if ($env:CACHE_BUILD_MODE -ne 'copy') { throw 'Unknown scope build fixture mode.' }
                $ready = [Threading.EventWaitHandle]::OpenExisting($env:CACHE_READY)
                $release = [Threading.EventWaitHandle]::OpenExisting($env:CACHE_RELEASE)
                try {
                    [void]$ready.Set()
                    if (-not $release.WaitOne(300000)) { throw 'Cache concurrent-build release event was not signaled.' }
                }
                finally { $ready.Dispose(); $release.Dispose() }
                $output = [regex]::Match($raw, '--output\s+(?:"(?<p>[^"]+)"|(?<p>\S+))').Groups['p'].Value
                if ([string]::IsNullOrWhiteSpace($output)) { throw 'Scope build omitted --output.' }
                [void][IO.Directory]::CreateDirectory($output)
                Get-ChildItem -LiteralPath $env:CACHE_SEED -Force | Copy-Item -Destination $output -Recurse -Force
                exit 0
            }
            $logger = [regex]::Match($raw, '(?i)(?:^|\s)"?-flp:LogFile=(?<p>.+?);Verbosity=normal;Encoding=UTF-8;Append=false"?(?:\s|$)')
            if (-not $logger.Success) { throw "Requested build omitted file logger: $raw" }
            [IO.File]::WriteAllText($logger.Groups['p'].Value, 'Build succeeded.')
            exit 0
            """);
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
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass" }.Concat(arguments)) info.ArgumentList.Add(argument);
        return info;
    }

    internal ProcessStartInfo CacheShell(string cwd, string mode, string log, params string[] arguments)
    {
        var info = Shell(cwd, arguments);
        info.Environment["PATH"] = Shim + Path.PathSeparator + info.Environment["PATH"];
        info.Environment["CACHE_DOTNET"] = _dotnet;
        info.Environment["CACHE_SHELL"] = WorkerShell.Executable;
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
        var info = CacheShell(work, mode, log, "-File", Path.Combine(Candidate, "scripts", "Invoke-WorkerBuildCheck.ps1"), Path.Combine(work, "One.csproj"));
        info.Environment["MCG_DOTNET_ISOLATED_ROOT"] = work + "-outputs";
        return info;
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
        var info = Shell(root);
        info.FileName = "git";
        info.ArgumentList.Clear();
        foreach (var argument in new[] { "-c", "core.fsmonitor=false", "-c", "gc.auto=0" }) info.ArgumentList.Add(argument);
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var result = Run(info);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        return result.Stdout;
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

    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    public void Dispose() => ScriptSandboxCleanup.DeleteOrThrow([Parent]);
}
