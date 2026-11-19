using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using static MtpTestRunnerScriptTests;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpTestRunnerScriptTestsNestedClassFilters
{
    [Fact]
    public void NestedClassFilters_MatchHostArguments()
    {
        string[] filters =
        [
            "FullyQualifiedName=Outer+Inner",
            "FullyQualifiedName~Outer+Inner",
            "FullyQualifiedName!=Outer+Inner",
            "FullyQualifiedName!~Outer+Inner",
            "FullyQualifiedName=Tests.Outer+Inner+Leaf"
        ];
        var root = RepositoryRoot();
        var module = Path.Combine(root, "scripts", "MtpTestRunner.psm1").Replace("'", "''", StringComparison.Ordinal);
        var command = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module '{{module}}' -Force
            $filters = ConvertFrom-Json -InputObject '{{JsonSerializer.Serialize(filters)}}'
            $invocation = [pscustomobject]@{ arguments = @('{executable}') }
            $results = foreach ($filter in $filters) {
                $token = & (Get-Module MtpTestRunner) { param($filter) Get-MtpFilterTokens -Filter $filter } $filter
                $arguments = @(New-MtpRunnerArguments -Invocation $invocation -Executable 'Tests.dll' -ResultsDirectory 'results' -TrxFileName 'lane.trx' -Filter $filter)
                @{ kind = $token.Kind; value = $token.Value; arguments = $arguments }
            }
            ConvertTo-Json -InputObject @($results) -Depth 4 -Compress
            """;

        var result = RunPowerShellCommand(root, command);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var output = JsonDocument.Parse(result.Stdout.Trim());
        Assert.Equal(filters.Length, output.RootElement.GetArrayLength());
        for (var index = 0; index < filters.Length; index++)
        {
            var filter = filters[index];
            var parsed = output.RootElement[index];
            Assert.Equal("Class", parsed.GetProperty("kind").GetString());
            Assert.Equal(filter[(filter.IndexOfAny(['=', '~']) + 1)..], parsed.GetProperty("value").GetString());
            var hostArguments = AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(filter);
            Assert.Equal(new[] { "Tests.dll" }.Concat(hostArguments).Concat(["--timeout", "780s"]),
                parsed.GetProperty("arguments").EnumerateArray().Select(argument => argument.GetString()));
        }
    }

    [Fact]
    public void Summary_RejectsUnsupportedPredicatesBeforeLaunchAndRunsNestedClassFilter()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var buildMarker = Path.Combine(sandbox.Root, "unsupported-filter-build.txt");
        var fakeDotnet = sandbox.CreateBuildStub(exitCode: 0, buildMarker);
        string[] unsupported = ["FullyQualifiedName=+Outer", "FullyQualifiedName~Outer/Inner", "TestCategory=Fast"];
        foreach (var filter in unsupported)
        {
            Assert.Throws<InvalidOperationException>(() =>
                AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(filter).ToArray());

            var rejected = sandbox.RunSummary(filter: filter, noBuild: false, dotnetPath: fakeDotnet);

            Assert.Equal(26, rejected.ExitCode);
            Assert.Contains("FILTER FAILURE - managed build/test processes were not launched", rejected.Stdout, StringComparison.Ordinal);
            Assert.Contains($"token '{filter}' at position 0 is not a supported predicate", rejected.Stdout, StringComparison.Ordinal);
            Assert.False(File.Exists(buildMarker));
            Assert.False(File.Exists(sandbox.ArgumentLog));
        }

        var accepted = sandbox.RunSummary(filter: "FullyQualifiedName=Outer+Inner");

        Assert.True(accepted.ExitCode == 0, accepted.Stdout + accepted.Stderr);
        Assert.DoesNotContain("FILTER FAILURE", accepted.Stdout, StringComparison.Ordinal);
        Assert.Contains("Outer+Inner", File.ReadAllLines(sandbox.ArgumentLog));
    }
}
