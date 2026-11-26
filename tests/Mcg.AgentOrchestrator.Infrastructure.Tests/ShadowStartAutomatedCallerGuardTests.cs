using System.Text.RegularExpressions;

public sealed class ShadowStartAutomatedCallerGuardTests
{
    [Fact]
    public void EveryTrackedInvocationSelectsRetentionExplicitly()
    {
        var root = VerifiedRepositoryRoot.Find();
        var result = InfrastructureTestSupport.RunGitProbe(root,
            ["grep", "-Ilz", "-i", "-e", "shadow-start", "--", ":(exclude)tests/**"]);
        Assert.True(result.Succeeded, result.StandardError);
        Assert.False(result.StandardOutputTruncated, "The tracked shadow-start path list exceeded the Git probe capture.");
        var candidates = result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("src/Mcg.AgentOrchestrator.App/Cli/CliShadowStartCommand.cs", candidates);

        var violations = candidates.Where(path =>
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
    [InlineData(".\\scripts\\Invoke-OrchestratorCommand.ps1 -AppDll $appDll shadow-start --source $source --scratch $scratch", true)]
    [InlineData(".\\scripts\\Invoke-OrchestratorCommand.ps1 -AppDll $appDll shadow-start --scratch $scratch # --scratch-retention disposable", true)]
    [InlineData(".\\scripts\\Invoke-OrchestratorCommand.ps1 -AppDll $appDll shadow-start --scratch 'copy #1' --scratch-retention disposable", false)]
    [InlineData(".\\scripts\\Invoke-OrchestratorCommand.ps1 -AppDll $appDll shadow-start --scratch 'copy --scratch-retention disposable'", true)]
    [InlineData("mcg-orchestrator.cmd shadow-start --scratch-retention=\"retain\"", false)]
    [InlineData(".\\scripts\\Invoke-RepoScript.ps1 scripts\\Invoke-OrchestratorCommand.ps1 -AppDll $appDll `\r\n shadow-start --scratch-retention disposable", false)]
    [InlineData(".\\scripts\\Invoke-OrchestratorCommand.ps1 -AppDll $appDll workspace list", false)]
    [InlineData("['shadow-start',\n '--scratch-retention', 'retain']", false)]
    [InlineData("[\n 'shadow-start',\n '--source', source, '--scratch', copy]", true)]
    [InlineData("Use shadow-start for the measurement.", false)]
    public void GuardRecognizesCommandAndArgumentListForms(string source, bool violation) =>
        Assert.Equal(violation, MissingRetention(source).Any());

    private static IEnumerable<string> MissingRetention(string source)
    {
        var logicalLines = Regex.Replace(source, """[`\\^][ \t]*\r?\n""", " ");
        const string quoted = """'[^'\r\n]*'|"[^"\r\n]*" """;
        const string shellTail = $$"""(?:{{quoted}}|[^"'#\r\n;|&])*""";
        var commands = Regex.Matches(logicalLines, $$"""
            (?ix)
            \b(?:mcg-orchestrator(?:\.(?:cmd|ps1|exe))?|Mcg\.AgentOrchestrator\.App\.(?:dll|exe))["']?\s+["']?shadow-start\b["']?(?<shell>{{shellTail}})
            | \bInvoke-OrchestratorCommand\.ps1["']?{{shellTail}}?[\x20\t]["']?shadow-start\b["']?(?<shell>{{shellTail}})
            | \[\s*["']shadow-start["'](?<array>[^\]]*)\]
            """);
        foreach (Match invocation in commands)
            if (!HasRetentionArgument(invocation))
                yield return invocation.Value;

        static bool HasRetentionArgument(Match invocation)
        {
            var array = invocation.Groups["array"];
            var unquoted = array.Success ? """[^\s,"'#]""" : """[^\s"'#]""";
            var arguments = Regex.Matches(array.Success ? array.Value : invocation.Groups["shell"].Value,
                    $$"""(?x) [#][^\r\n]* | (?<arg>(?:{{quoted}}|{{unquoted}})+)""")
                .Select(match => match.Groups["arg"])
                .Where(group => group.Success)
                .Select(group => group.Value.Trim('\'', '"').Replace("=\"", "=").Replace("='", "="))
                .ToArray();
            for (var i = 0; i < arguments.Length; i++)
            {
                var argument = arguments[i].ToLowerInvariant();
                if (argument is "--scratch-retention=disposable" or "--scratch-retention=retain" ||
                    argument == "--scratch-retention" && i + 1 < arguments.Length &&
                    arguments[i + 1].ToLowerInvariant() is "disposable" or "retain")
                    return true;
            }
            return false;
        }
    }
}
