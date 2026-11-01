using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using static MtpTestRunnerScriptTests;

// Child PowerShell invocations use the existing process fixture; files belong to this test.
[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpTestRunnerScriptTestsExactClassSelectors : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exact-class-filter-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("FullyQualifiedName=Tests.A|FullyQualifiedName=Tests.B")]
    [InlineData("FullyQualifiedName!=Tests.A&FullyQualifiedName!=Tests.B")]
    [InlineData("(FullyQualifiedName=Tests.A|FullyQualifiedName~Tests.B)&Category!=HostIntegration")]
    public void ExactSelectors_MatchTheGateTranslation_WithoutAddedWildcards(string filter)
    {
        Assert.Equal(AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(filter), TranslateMtpFilter(filter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeLaneFilters_SpillLosslessly_BelowTheNativeLaunchLimit(bool residual)
    {
        Directory.CreateDirectory(_root);
        var names = Enumerable.Range(0, 1200).Select(index => $"Tests.Infrastructure.Fixture{index:D4}Tests").ToArray();
        var filter = residual ? string.Join('&', names.Select(name => "FullyQualifiedName!=" + name)) :
            string.Join('|', names.Take(300).Select(name => "FullyQualifiedName=" + name));
        var filterFile = Path.Combine(_root, "filter.txt");
        File.WriteAllText(filterFile, filter);
        var command = $$"""
            Import-Module '{{Quote(Path.Combine(RepositoryRoot(), "scripts", "MtpTestRunner.psm1"))}}' -Force
            $invocation = [pscustomobject]@{ arguments = @('{executable}', '--results-directory', '{resultsDirectory}', '--report-trx-filename', '{trxFileName}') }
            $filter = [IO.File]::ReadAllText('{{Quote(filterFile)}}')
            $argv = @(New-MtpRunnerArguments -Invocation $invocation -Executable '{{Quote(Path.Combine(_root, "Tests.dll"))}}' -ResultsDirectory '{{Quote(_root)}}' -TrxFileName 'lane.trx' -Filter $filter)
            $start = & (Get-Module MtpTestRunner) { param($argv) New-MtpProcessStartInfo -Executable 'dotnet' -Arguments (@('dotnet') + $argv) } -argv $argv
            [ordered]@{ arguments = $argv; commandLength = $start.FileName.Length + 1 + $start.Arguments.Length } | ConvertTo-Json -Compress
            """;
        var result = RunPowerShellCommand(RepositoryRoot(), command);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var output = JsonDocument.Parse(result.Stdout.Trim());
        var arguments = output.RootElement.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()!).ToArray();
        var response = Assert.Single(arguments, argument => argument.StartsWith('@'))[1..];
        Assert.InRange(output.RootElement.GetProperty("commandLength").GetInt32(), 1, 4096);
        Assert.Equal(AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(filter), File.ReadAllLines(response));
        Assert.DoesNotContain("--filter-class", arguments);
        Assert.DoesNotContain("--filter-not-class", arguments);
    }

    [Fact]
    public void FilterFile_InvalidGrammar_IsReadBeforeAnyBuildOrTestLaunch()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "filter.txt");
        File.WriteAllText(path, "FullyQualifiedName~Alpha|Name~Method");
        var summary = Path.Combine(RepositoryRoot(), "scripts", "Invoke-TestSummary.ps1");
        var result = RunPowerShellCommand(RepositoryRoot(),
            $"& '{Quote(summary)}' -FilterFile '{Quote(path)}' -ResultsRoot '{Quote(Path.Combine(_root, "results"))}'");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("FILTER FAILURE - managed build/test processes were not launched", result.Stdout);
        Assert.Contains("joins different predicate kinds", result.Stdout);
        Assert.False(Directory.Exists(Path.Combine(_root, "results")));
    }

    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
