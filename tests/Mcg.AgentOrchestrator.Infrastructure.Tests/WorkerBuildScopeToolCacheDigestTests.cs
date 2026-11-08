using System.Text.Json;
using System.Text.RegularExpressions;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerBuildScopeToolCacheDigestTests
{
    private readonly Xunit.ITestOutputHelper _output;
    public WorkerBuildScopeToolCacheDigestTests(Xunit.ITestOutputHelper output) => _output = output;

    private sealed record Mutation(string Relative, string Operation, bool Invalidates = true);

    private static readonly Mutation[] Cases =
    [
        new("scripts/WorkerBuildScopeTool/Program.cs", "edit"),
        new("scripts/WorkerBuildScopeTool/new source ü.cs", "add"),
        new("scripts/WorkerBuildScopeTool/Program.cs", "delete"),
        new("scripts/WorkerBuildScopeTool/Program.cs", "rename"),
        new("scripts/WorkerBuildScopeTool/WorkerBuildScopeTool.csproj", "edit"),
        new("src/Mcg.AgentOrchestrator.Execution/Mcg.AgentOrchestrator.Execution.csproj", "edit"),
        new("src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj", "edit"),
        new("src/Mcg.AgentOrchestrator.Infrastructure.Providers/Mcg.AgentOrchestrator.Infrastructure.Providers.csproj", "edit"),
        new("src/Mcg.AgentOrchestrator.Execution/Logic.cs", "add"),
        new("src/Mcg.AgentOrchestrator.Core/Logic.cs", "add"),
        new("src/Mcg.AgentOrchestrator.Infrastructure.Providers/Logic.cs", "add"),
        new("Directory.Build.props", "edit"),
        new("Directory.Build.targets", "edit"),
        new("Directory.Build.rsp", "edit"),
        new("Directory.Packages.props", "add"),
        new("global.json", "edit"),
        new("scripts/Directory.Build.props", "add"),
        new("outside-closure.txt", "add", false),
        new("scripts/WorkerBuildScopeTool/bin/ignored.txt", "add", false)
    ];

    [Xunit.Fact]
    public void SourceInputMutations_ClosureAndAncestorBoundary_InvalidatesOnlyInputs()
    {
        using var fixture = new WorkerBuildScopeToolCacheFixture();
        var cases = Path.Combine(fixture.Parent, "cases.json");
        File.WriteAllText(cases, JsonSerializer.Serialize(Cases));
        var driver = Path.Combine(fixture.Parent, "resolve.ps1");
        // One owned process exercises every mutation and its restored control.
        // Only the external dotnet command is stubbed; digest and cache decisions
        // use the real script, git index and working tree for every case.
        File.WriteAllText(driver, """
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            . $env:CACHE_STUB
            . (Join-Path $env:CACHE_HOME 'scripts/WorkerBuildScopeToolCache.ps1')
            $project = Join-Path $env:CACHE_HOME 'scripts/WorkerBuildScopeTool/WorkerBuildScopeTool.csproj'
            function Resolve-Fixture {
                Resolve-WorkerBuildScopeTool -ToolProject $project -WorkingDirectory $env:CACHE_HOME -BuildRoot (Join-Path $env:CACHE_PARENT 'build') -BuildBin (Join-Path $env:CACHE_PARENT 'build/bin') -BootstrapLog (Join-Path $env:CACHE_PARENT 'bootstrap.txt')
            }
            function Build-Count {
                if (-not [IO.File]::Exists($env:CACHE_VECTOR_LOG)) { return 0 }
                return @([IO.File]::ReadAllLines($env:CACHE_VECTOR_LOG)).Count
            }
            $baseline = Resolve-Fixture
            if ($baseline.Failure) { throw $baseline.Failure }
            $baselineBuilds = Build-Count
            $manifest = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $baseline.Dll) 'manifest.json') -Raw | ConvertFrom-Json
            [IO.File]::Delete($env:CACHE_VECTOR_LOG)
            $control = Resolve-Fixture
            $controlBuilds = Build-Count
            $results = @(
                foreach ($case in (Get-Content -LiteralPath $env:CACHE_CASES -Raw | ConvertFrom-Json)) {
                    [IO.File]::Delete($env:CACHE_VECTOR_LOG)
                    $target = Join-Path $env:CACHE_HOME $case.Relative
                    $previous = if ([IO.File]::Exists($target)) { [IO.File]::ReadAllBytes($target) } else { $null }
                    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                    switch ($case.Operation) {
                        'edit' {
                            if ($null -eq $previous) { throw "Missing mutation input: $target" }
                            $suffix = if ($case.Relative -match '\.(csproj|props|targets)$') { "`n<!-- cache digest mutation -->`n" }
                                elseif ($case.Relative -eq 'global.json') { "`n " } else { "`n// cache digest mutation`n" }
                            [IO.File]::AppendAllText($target, $suffix)
                        }
                        'add' { [IO.File]::WriteAllText($target, 'new cache digest input') }
                        'delete' { [IO.File]::Delete($target) }
                        'rename' { $null = Invoke-WorkerBuildScopeGit -Root $env:CACHE_HOME -Arguments @('mv', '--', $case.Relative, ($case.Relative + '.renamed')) }
                        default { throw "Unknown mutation: $($case.Operation)" }
                    }
                    $env:CACHE_BUILD_MODE = 'fail'
                    $mutated = Resolve-Fixture
                    $builds = Build-Count
                    $entryCount = @([IO.Directory]::GetDirectories($env:MCG_WORKER_BUILD_SCOPE_TOOL_CACHE)).Count
                    if ($case.Operation -eq 'rename') {
                        $null = Invoke-WorkerBuildScopeGit -Root $env:CACHE_HOME -Arguments @('mv', '--', ($case.Relative + '.renamed'), $case.Relative)
                    }
                    elseif ($null -eq $previous) { [IO.File]::Delete($target) }
                    else { [IO.File]::WriteAllBytes($target, [byte[]]$previous) }
                    $restored = Resolve-Fixture
                    [pscustomobject]@{
                        Relative = $case.Relative; Operation = $case.Operation
                        Line = $mutated.Line; Failure = $mutated.Failure; Builds = $builds; EntryCount = $entryCount
                        RestoredLine = $restored.Line; RestoredFailure = $restored.Failure; RestoredBuilds = (Build-Count)
                    }
                }
            )
            [pscustomobject]@{
                Digest = $manifest.sourceDigest; BaselineLine = $baseline.Line; BaselineBuilds = $baselineBuilds
                ControlLine = $control.Line; ControlFailure = $control.Failure; ControlBuilds = $controlBuilds; Cases = $results
            } | ConvertTo-Json -Depth 6 -Compress
            exit 0
            """);
        var start = fixture.CacheShell(fixture.Home, "stub", Path.Combine(fixture.Parent, "vectors.txt"), "-File", driver);
        start.Environment["CACHE_PARENT"] = fixture.Parent;
        start.Environment["CACHE_CASES"] = cases;
        var result = WorkerBuildScopeToolCacheFixture.Run(start);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        var digest = root.GetProperty("Digest").GetString()!;
        Assert.Matches("^[a-f0-9]{64}$", digest);
        var hit = "scope-tool: cache=hit digest=" + digest[..12];
        Assert.Matches(@"^scope-tool: cache=miss digest=[a-f0-9]{12} build_ms=\d+$", root.GetProperty("BaselineLine").GetString()!);
        Assert.Equal(1, root.GetProperty("BaselineBuilds").GetInt32());
        Assert.Equal(hit, root.GetProperty("ControlLine").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ControlFailure").ValueKind);
        Assert.Equal(0, root.GetProperty("ControlBuilds").GetInt32());
        var rows = root.GetProperty("Cases").EnumerateArray().ToArray();
        Assert.Equal(Cases.Length, rows.Length);
        for (var index = 0; index < Cases.Length; index++)
        {
            var mutation = Cases[index];
            var row = rows[index];
            _output.WriteLine(row.GetRawText());
            Assert.Equal(mutation.Relative, row.GetProperty("Relative").GetString());
            Assert.Equal(mutation.Operation, row.GetProperty("Operation").GetString());
            var line = row.GetProperty("Line").GetString()!;
            if (mutation.Invalidates)
            {
                Assert.Matches(@"^scope-tool: cache=miss digest=[a-f0-9]{12} build_ms=\d+$", line);
                Assert.NotEqual(digest[..12], Regex.Match(line, @"digest=([a-f0-9]{12})").Groups[1].Value);
                Assert.Contains("worker build scope tool bootstrap failed", row.GetProperty("Failure").GetString()!);
                Assert.Equal(1, row.GetProperty("Builds").GetInt32());
            }
            else
            {
                Assert.Equal(hit, line);
                Assert.Equal(JsonValueKind.Null, row.GetProperty("Failure").ValueKind);
                Assert.Equal(0, row.GetProperty("Builds").GetInt32());
            }
            Assert.Equal(1, row.GetProperty("EntryCount").GetInt32()); // failed builds publish nothing
            Assert.Equal(hit, row.GetProperty("RestoredLine").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("RestoredFailure").ValueKind);
            Assert.Equal(row.GetProperty("Builds").GetInt32(), row.GetProperty("RestoredBuilds").GetInt32());
        }
        WorkerBuildScopeToolCacheFixture.ValidEntry(Assert.Single(Directory.GetDirectories(fixture.Cache)));
    }
}
