using System.Text.Json;
using static MtpTestRunnerScriptTests;

internal sealed class MtpManagedFilterDiscoveryResultFile : IDisposable
{
    private readonly string directory;

    private MtpManagedFilterDiscoveryResultFile(string directory)
    {
        this.directory = directory;
        Path = System.IO.Path.Combine(directory, "result.json");
        Assert.False(File.Exists(Path), $"Result file must not preexist: '{Path}'");
    }

    public string Path { get; }

    public static MtpManagedFilterDiscoveryResultFile Create() =>
        new(SharedTestSupport.CreateTempDirectory());

    public string WriteJsonStatement(string jsonExpression)
    {
        var escapedPath = Path.Replace("'", "''", StringComparison.Ordinal);
        return $"[System.IO.File]::WriteAllText('{escapedPath}', [string]({jsonExpression}), " +
            "[System.Text.UTF8Encoding]::new($false))";
    }

    public JsonDocument Read(ProcessResult result)
    {
        Assert.True(result.ExitCode == 0, FailureMessage("command failed", result));
        Assert.True(File.Exists(Path), FailureMessage("file missing", result));

        string json;
        try
        {
            // ReadAllText detects a UTF-8 BOM as well as the writer's BOM-free UTF-8.
            json = File.ReadAllText(Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Fail(FailureMessage($"file unreadable: {exception.Message}", result));
            throw;
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            Assert.Fail(FailureMessage($"invalid JSON: {exception.Message}\nfile contents:\n{json}", result));
            throw;
        }
    }

    public static string[] TranslateMtpFilter(string filter)
    {
        var root = RepositoryRoot();
        var module = System.IO.Path.Combine(root, "scripts", "MtpTestRunner.psm1")
            .Replace("'", "''", StringComparison.Ordinal);
        var escapedFilter = filter.Replace("'", "''", StringComparison.Ordinal);
        using var resultFile = Create();
        var command = $"Import-Module '{module}' -Force; " +
            $"$json = @(ConvertTo-MtpFilterArguments -Filter '{escapedFilter}') | ConvertTo-Json -Compress; " +
            resultFile.WriteJsonStatement("$json");
        var result = RunPowerShellCommand(root, command);
        using var document = resultFile.Read(result);
        return document.RootElement.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private string FailureMessage(string reason, ProcessResult result) =>
        $"MTP translator result {reason}: '{Path}'\nexit={result.ExitCode}\n" +
        $"stdout:\n{result.Stdout}\nstderr:\n{result.Stderr}";

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(directory);
}
