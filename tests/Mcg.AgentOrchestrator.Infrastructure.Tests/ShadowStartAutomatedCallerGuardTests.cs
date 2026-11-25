using System.Diagnostics;
using System.Text.RegularExpressions;

public sealed class ShadowStartAutomatedCallerGuardTests
{
    [Fact]
    public void EveryTrackedInvocationSelectsRetentionExplicitly()
    {
        var root = VerifiedRepositoryRoot.Find();
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-c", "core.fsmonitor=false", "grep", "-Ilz", "-i", "-e", "shadow-start", "--" })
            start.ArgumentList.Add(argument);
        var result = TestChildProcessCapture.Run(start);
        Assert.True(result.ExitCode == 0, result.Stderr);
        var candidates = result.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("src/Mcg.AgentOrchestrator.App/Cli/CliShadowStartCommand.cs", candidates);

        var violations = candidates.Where(path =>
                !path.StartsWith("tests/", StringComparison.Ordinal) &&
                path is not ("src/Mcg.AgentOrchestrator.App/Cli/CliShadowStartCommand.cs" or
                    "src/Mcg.AgentOrchestrator.App/Cli/CliShadowStartCommandHelp.cs"))
            .SelectMany(path => MissingRetention(File.ReadAllText(Path.Combine(root, path))).Select(line => $"{path}: {line}"))
            .ToArray();
        Assert.True(violations.Length == 0, $"Select --scratch-retention explicitly in:{Environment.NewLine}" +
            string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData("mcg-orchestrator.cmd shadow-start --source path --scratch copy", true)]
    [InlineData("dotnet Mcg.AgentOrchestrator.App.dll shadow-start --scratch-retention=disposable", false)]
    [InlineData("['shadow-start',\n '--scratch-retention', 'retain']", false)]
    [InlineData("[\n 'shadow-start',\n '--source', source, '--scratch', copy]", true)]
    [InlineData("Use shadow-start for the measurement.", false)]
    public void GuardRecognizesCommandAndArgumentListForms(string source, bool violation) =>
        Assert.Equal(violation, MissingRetention(source).Any());

    private static IEnumerable<string> MissingRetention(string source)
    {
        var logicalLines = Regex.Replace(source, """[`\\^][ \t]*\r?\n""", " ");
        var commands = Regex.Matches(logicalLines,
            """(?im)\b(?:mcg-orchestrator(?:\.(?:cmd|ps1|exe))?|Mcg\.AgentOrchestrator\.App\.(?:dll|exe))["']?\s+["']?shadow-start\b[^\r\n;|&]*|\[\s*["']shadow-start["'][^\]]*\]""");
        foreach (Match invocation in commands)
            if (!Regex.IsMatch(invocation.Value, """(?i)--scratch-retention["']?(?:=|\s|,)+["']?(?:disposable|retain)\b"""))
                yield return invocation.Value;
    }
}
