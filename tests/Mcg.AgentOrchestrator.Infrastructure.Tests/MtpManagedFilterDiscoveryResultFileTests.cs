using System.Text;
using System.Text.Json;
using Xunit.Sdk;
using static MtpTestRunnerScriptTests;

// Parallel-safe: commands and file locks use only a fresh, test-owned directory.
public sealed class MtpManagedFilterDiscoveryResultFileTests
{
    [Xunit.Fact]
    public void Read_WarningAfterStdoutJson_ParsesResultFile()
    {
        using var resultFile = MtpManagedFilterDiscoveryResultFile.Create();
        var marker = Guid.NewGuid().ToString("N");
        var json = JsonSerializer.Serialize(new { marker, args = new[] { "--filter-class", "*X*" } });
        var escapedJson = json.Replace("'", "''", StringComparison.Ordinal);
        var command = $"$json = '{escapedJson}'; " + resultFile.WriteJsonStatement("$json") + "; " +
            "[Console]::Out.WriteLine($json); " +
            "[Console]::Out.WriteLine('WARNING: synthetic translator warning')";
        var result = RunPowerShellCommand(RepositoryRoot(), command);

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        var jsonIndex = result.Stdout.IndexOf(json, StringComparison.Ordinal);
        var warningIndex = result.Stdout.IndexOf("WARNING: synthetic translator warning", StringComparison.Ordinal);
        Assert.True(jsonIndex >= 0, result.Stdout);
        Assert.True(warningIndex > jsonIndex + json.Length, result.Stdout);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(result.Stdout.Trim()));

        using var document = resultFile.Read(result);
        Assert.Equal(marker, document.RootElement.GetProperty("marker").GetString());
        Assert.Equal(new[] { "--filter-class", "*X*" }, document.RootElement.GetProperty("args")
            .EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    [Xunit.Fact]
    public void Read_MissingFile_FailsWithBothCapturedStreams()
    {
        using var resultFile = MtpManagedFilterDiscoveryResultFile.Create();
        var result = RunWithDiagnostics("");

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(resultFile.Path));
        AssertFailureDiagnostics(resultFile, result, "file missing");
    }

    [Xunit.Fact]
    public void Read_InvalidFile_FailsWithBothCapturedStreams()
    {
        using var resultFile = MtpManagedFilterDiscoveryResultFile.Create();
        var result = RunWithDiagnostics(resultFile.WriteJsonStatement("'not-json{'"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("not-json{", File.ReadAllText(resultFile.Path));
        AssertFailureDiagnostics(resultFile, result, "invalid JSON");
    }

    [Xunit.Fact]
    public void Read_NonzeroExitWithValidFile_FailsWithBothCapturedStreams()
    {
        using var resultFile = MtpManagedFilterDiscoveryResultFile.Create();
        var result = RunWithDiagnostics(resultFile.WriteJsonStatement("'{}'") + "; exit 7");

        Assert.Equal(7, result.ExitCode);
        using var validJson = JsonDocument.Parse(File.ReadAllText(resultFile.Path));
        Assert.Equal(JsonValueKind.Object, validJson.RootElement.ValueKind);
        AssertFailureDiagnostics(resultFile, result, "command failed");
    }

    [Xunit.Fact]
    public void Read_LockedFile_FailsWithBothCapturedStreams()
    {
        using var resultFile = MtpManagedFilterDiscoveryResultFile.Create();
        File.WriteAllText(resultFile.Path, "{}", new UTF8Encoding(true));
        using var fileLock = new FileStream(resultFile.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = new ProcessResult(0, "locked-file-stdout", "locked-file-stderr");

        AssertFailureDiagnostics(resultFile, result, "file unreadable");
    }

    [Xunit.Fact]
    public void Read_Utf8Bom_ParsesResultFile()
    {
        using var resultFile = MtpManagedFilterDiscoveryResultFile.Create();
        File.WriteAllText(resultFile.Path, "{\"marker\":\"caf\u00e9\"}", new UTF8Encoding(true));

        using var document = resultFile.Read(new ProcessResult(0, "", ""));

        Assert.Equal("caf\u00e9", document.RootElement.GetProperty("marker").GetString());
    }

    private static ProcessResult RunWithDiagnostics(string command)
    {
        var marker = Guid.NewGuid().ToString("N");
        var result = RunPowerShellCommand(RepositoryRoot(),
            $"[Console]::Out.WriteLine('stdout-{marker}'); " +
            $"[Console]::Error.WriteLine('stderr-{marker}'); " + command);
        Assert.Contains($"stdout-{marker}", result.Stdout, StringComparison.Ordinal);
        Assert.Contains($"stderr-{marker}", result.Stderr, StringComparison.Ordinal);
        return result;
    }

    private static void AssertFailureDiagnostics(
        MtpManagedFilterDiscoveryResultFile resultFile, ProcessResult result, string reason)
    {
        var exception = Assert.ThrowsAny<XunitException>(() => resultFile.Read(result));
        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
        Assert.Contains(resultFile.Path, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"exit={result.ExitCode}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("stdout:", exception.Message, StringComparison.Ordinal);
        Assert.Contains(result.Stdout, exception.Message, StringComparison.Ordinal);
        Assert.Contains("stderr:", exception.Message, StringComparison.Ordinal);
        Assert.Contains(result.Stderr, exception.Message, StringComparison.Ordinal);
    }
}
