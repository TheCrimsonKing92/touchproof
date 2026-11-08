using System.Text.Json;
using System.Text.RegularExpressions;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerBuildScopeToolCacheDigestTests
{
    [Xunit.Theory]
    [Xunit.InlineData("scripts/WorkerBuildScopeTool/Program.cs", "edit", true)]
    [Xunit.InlineData("scripts/WorkerBuildScopeTool/new source ü.cs", "add", true)]
    [Xunit.InlineData("scripts/WorkerBuildScopeTool/Program.cs", "delete", true)]
    [Xunit.InlineData("scripts/WorkerBuildScopeTool/Program.cs", "rename", true)]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Execution/Mcg.AgentOrchestrator.Execution.csproj", "edit", true)]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj", "edit", true)]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure.Providers/Mcg.AgentOrchestrator.Infrastructure.Providers.csproj", "edit", true)]
    [Xunit.InlineData("Directory.Build.props", "edit", true)]
    [Xunit.InlineData("Directory.Build.targets", "edit", true)]
    [Xunit.InlineData("Directory.Build.rsp", "edit", true)]
    [Xunit.InlineData("Directory.Packages.props", "add", true)]
    [Xunit.InlineData("global.json", "edit", true)]
    [Xunit.InlineData("scripts/Directory.Build.props", "add", true)]
    [Xunit.InlineData("outside-closure.txt", "add", false)]
    [Xunit.InlineData("scripts/WorkerBuildScopeTool/bin/ignored.txt", "add", false)]
    public void SourceInputMutation_ClosureAndAncestorBoundary_InvalidatesOnlyInputs(
        string relative, string operation, bool invalidates)
    {
        using var fixture = new WorkerBuildScopeToolCacheFixture();
        var home = Path.Combine(fixture.Parent, "home");
        Directory.CreateDirectory(home);
        // Copy only git-visible files from the candidate. Outputs never enter the
        // home copy, and a new private index establishes a controlled clean baseline.
        var files = fixture.Git(fixture.Candidate, "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--",
            "scripts/WorkerBuildScopeTool", "scripts/WorkerBuildScopeToolCache.ps1",
            "src/Mcg.AgentOrchestrator.Execution", "src/Mcg.AgentOrchestrator.Core", "src/Mcg.AgentOrchestrator.Infrastructure.Providers",
            "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp", "Directory.Packages.props", "global.json", ".gitignore");
        foreach (var file in files.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
        {
            var destination = Path.Combine(home, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(fixture.Candidate, file), destination);
        }
        fixture.InitGit(home);
        var driver = Path.Combine(fixture.Parent, "resolve.ps1");
        File.WriteAllText(driver, """
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            . (Join-Path $env:CACHE_HOME 'scripts/WorkerBuildScopeToolCache.ps1')
            $project = Join-Path $env:CACHE_HOME 'scripts/WorkerBuildScopeTool/WorkerBuildScopeTool.csproj'
            if ($env:CACHE_DIGEST_ONLY -eq '1') {
                Get-WorkerBuildScopeToolDigest -ToolProject $project -WorkingDirectory $env:CACHE_HOME
                exit 0
            }
            $result = Resolve-WorkerBuildScopeTool -ToolProject $project -WorkingDirectory $env:CACHE_HOME -BuildRoot (Join-Path $env:CACHE_PARENT 'build') -BuildBin (Join-Path $env:CACHE_PARENT 'build/bin') -BootstrapLog (Join-Path $env:CACHE_PARENT 'bootstrap.txt')
            Write-Output $result.Line
            if ($result.Failure) { Write-Output $result.Failure; exit 9 }
            exit 0
            """);
        var log = Path.Combine(fixture.Parent, "vectors.txt");
        TestChildProcessCapture.Result Resolve(bool digestOnly = false)
        {
            var start = fixture.CacheShell(home, "fail", log, "-File", driver);
            start.Environment["CACHE_HOME"] = home;
            start.Environment["CACHE_PARENT"] = fixture.Parent;
            start.Environment["CACHE_DIGEST_ONLY"] = digestOnly ? "1" : "0";
            return WorkerBuildScopeToolCacheFixture.Run(start);
        }
        var baseline = Resolve(digestOnly: true);
        Assert.True(baseline.ExitCode == 0, baseline.Stdout + baseline.Stderr);
        var digest = baseline.Stdout.Trim();
        Assert.Matches("^[a-f0-9]{64}$", digest);
        var entry = Path.Combine(fixture.Cache, digest);
        Directory.CreateDirectory(entry);
        var dll = Path.Combine(entry, "WorkerBuildScopeTool.dll");
        File.WriteAllText(dll, "Controlled cache-entry bytes; Resolve does not execute the DLL.");
        File.WriteAllText(Path.Combine(entry, "WorkerBuildScopeTool.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(entry, "WorkerBuildScopeTool.deps.json"), "{}");
        File.WriteAllText(Path.Combine(entry, "manifest.json"), JsonSerializer.Serialize(new { sourceDigest = digest, dllSha256 = WorkerBuildScopeToolCacheFixture.Hash(dll) }));
        WorkerBuildScopeToolCacheFixture.ValidEntry(entry);
        var control = Resolve();
        Assert.True(control.ExitCode == 0, control.Stdout + control.Stderr);
        Assert.Equal("scope-tool: cache=hit digest=" + digest[..12], control.Stdout.Trim());
        Assert.False(File.Exists(log), "Unchanged-home control unexpectedly invoked a bootstrap.");

        var target = Path.Combine(home, relative);
        var previous = File.Exists(target) ? File.ReadAllBytes(target) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        switch (operation)
        {
            case "edit":
                Assert.NotNull(previous);
                // Preserve valid project XML and SDK JSON while changing content.
                File.AppendAllText(target, relative.EndsWith(".csproj", StringComparison.Ordinal) || relative.EndsWith(".props", StringComparison.Ordinal) || relative.EndsWith(".targets", StringComparison.Ordinal)
                    ? "\n<!-- cache digest mutation -->\n" : relative == "global.json" ? "\n " : "\n// cache digest mutation\n");
                break;
            case "add": File.WriteAllText(target, "new cache digest input"); break;
            case "delete": File.Delete(target); break;
            case "rename": fixture.Git(home, "mv", relative, relative + ".renamed"); break;
            default: throw new InvalidOperationException(operation);
        }
        var mutated = Resolve();
        var line = mutated.Stdout.Trim();
        if (invalidates)
        {
            Assert.Equal(9, mutated.ExitCode);
            Assert.Matches(@"^scope-tool: cache=miss digest=[a-f0-9]{12} build_ms=\d+", line);
            Assert.NotEqual(digest[..12], Regex.Match(line, @"digest=([a-f0-9]{12})").Groups[1].Value);
            Assert.Single(File.ReadAllLines(log)); // failed bootstrap proves the miss branch ran
            Assert.Single(Directory.GetDirectories(fixture.Cache)); // no failed-build publication
        }
        else
        {
            Assert.True(mutated.ExitCode == 0, mutated.Stdout + mutated.Stderr);
            Assert.Equal(control.Stdout, mutated.Stdout);
            Assert.False(File.Exists(log));
        }
        if (operation == "rename") fixture.Git(home, "mv", relative + ".renamed", relative);
        else if (previous is null) File.Delete(target);
        else File.WriteAllBytes(target, previous);
        var restored = Resolve();
        Assert.True(restored.ExitCode == 0, restored.Stdout + restored.Stderr);
        Assert.Equal(control.Stdout, restored.Stdout);
    }
}
