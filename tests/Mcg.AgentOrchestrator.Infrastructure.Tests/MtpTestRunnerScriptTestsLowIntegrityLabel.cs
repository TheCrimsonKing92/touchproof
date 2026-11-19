using System.Text.Json;

// Uses the ProcessSpawning collection for the real PowerShell child. Label operations are fixture-owned.
[Collection(TestCollections.ProcessSpawning)]
public sealed class MtpTestRunnerScriptTestsLowIntegrityLabel : IDisposable
{
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Fact]
    public void SetLabel_CreatesDirectoryAndSetsLowOnlyOnce()
    {
        using var observation = RunInModule("""
            $directory = Join-Path $fixture 'unlabeled directory'
            $state = @{ low = $false; calls = @(); reads = @() }
            $reader = {
                param($path)
                $state.reads += (Test-Path -LiteralPath $path -PathType Container)
                return $state.low
            }
            $runner = {
                param($path, $operation, $label)
                $state.calls += ,@($path, $operation, $label)
                $state.low = $true
                return 0
            }
            Set-MtpLowIntegrityLabel -Path $directory -LabelReader $reader -IcaclsRunner $runner
            Set-MtpLowIntegrityLabel -Path $directory -LabelReader $reader -IcaclsRunner $runner
            $state
            """);

        var result = observation.RootElement;
        Assert.Equal(new[] { true, true }, result.GetProperty("reads").EnumerateArray().Select(read => read.GetBoolean()));
        var call = Assert.Single(result.GetProperty("calls").EnumerateArray());
        Assert.Equal(new[] { Path.Combine(_root, "unlabeled directory"), "/setintegritylevel", "(OI)(CI)low" },
            call.EnumerateArray().Select(argument => argument.GetString()));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 5)]
    [InlineData(3, 0)]
    public void Initialize_LabelsDefaultParentBeforeCreatingCustomResults(int readExit, int setExit)
    {
        using var observation = RunInModule($$"""
            $env:LOCALAPPDATA = $fixture
            $defaultRoot = Get-DefaultMtpResultsRoot
            $results = Join-Path $defaultRoot 'custom'
            $script:labelFixture = @{ readPath = $null; args = @(); parentExists = $false; defaultExists = $false; resultsExist = $false }
            Set-Item -Path 'Function:script:Test-MtpLowIntegrityLabel' -Value {
                param($Path)
                $script:labelFixture.readPath = $Path
                if ({{readExit}} -ne 0) { throw 'icacls.exe exited with code {{readExit}}.' }
                return $false
            }
            Set-Item -Path 'Function:script:Invoke-MtpIcacls' -Value {
                $script:labelFixture.args = @($args)
                $script:labelFixture.parentExists = Test-Path -LiteralPath $args[0] -PathType Container
                $script:labelFixture.defaultExists = Test-Path -LiteralPath (Join-Path $args[0] 'mcg-tests')
                $script:labelFixture.resultsExist = Test-Path -LiteralPath (Join-Path $args[0] 'mcg-tests\custom')
                return {{setExit}}
            }
            $script:labelFixture.error = $null
            $script:labelFixture.runDirectory = $null
            try {
                $script:labelFixture.runDirectory = Initialize-MtpResultsDirectory -ResultsRoot $results -RunLabel 'label'
            }
            catch { $script:labelFixture.error = $_.Exception.Message }
            $script:labelFixture
            """);

        var result = observation.RootElement;
        var parent = Path.Combine(_root, "Temp", "Low");
        var defaultRoot = Path.Combine(parent, "mcg-tests");
        Assert.Equal(parent, result.GetProperty("readPath").GetString());
        var arguments = result.GetProperty("args").EnumerateArray();
        if (readExit == 0)
        {
            Assert.Equal(new[] { parent, "/setintegritylevel", "(OI)(CI)low" }, arguments.Select(arg => arg.GetString()));
            Assert.True(result.GetProperty("parentExists").GetBoolean());
            Assert.False(result.GetProperty("defaultExists").GetBoolean());
            Assert.False(result.GetProperty("resultsExist").GetBoolean());
        }
        else
        {
            Assert.Empty(arguments);
        }

        var error = result.GetProperty("error").GetString();
        var runDirectory = result.GetProperty("runDirectory").GetString();
        if (readExit == 0 && setExit == 0)
        {
            Assert.Null(error);
            Assert.NotNull(runDirectory);
            Assert.Equal(Path.Combine(defaultRoot, "custom"), Path.GetDirectoryName(runDirectory));
            Assert.True(Directory.Exists(runDirectory), runDirectory);
        }
        else
        {
            Assert.NotNull(error);
            Assert.StartsWith(readExit != 0 ? "Could not read" : "Could not set", error, StringComparison.Ordinal);
            Assert.Contains(parent, error, StringComparison.Ordinal);
            Assert.Contains($"icacls.exe exited with code {readExit + setExit}", error, StringComparison.Ordinal);
            Assert.Null(runDirectory);
            Assert.False(Directory.Exists(defaultRoot));
        }
    }

    private JsonDocument RunInModule(string body)
    {
        var repo = MtpTestRunnerScriptTests.RepositoryRoot();
        var script = Path.Combine(repo, "scripts", "MtpTestRunner.psm1");
        var command = $$"""
            $ErrorActionPreference = 'Stop'
            $module = Import-Module '{{Quote(script)}}' -Force -PassThru -WarningAction SilentlyContinue
            & $module {
                $fixture = '{{Quote(_root)}}'
                {{body}}
            } | ConvertTo-Json -Depth 5 -Compress
            """;
        var result = MtpTestRunnerScriptTests.RunPowerShellCommand(repo, command);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        return JsonDocument.Parse(result.Stdout);
    }

    private static string Quote(string path) => path.Replace("'", "''", StringComparison.Ordinal);

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
