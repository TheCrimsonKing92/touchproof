using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceMissingToolClassifier
{
    private const string WindowsPhrase = "is not recognized as an internal or external command";
    private const string PosixPhrase = "command not found";
    private const string ToolchainPhrase = "could not choose a version of";
    private const string ToolPattern = """(?:'(?<tool>[^'\r\n]+)'|"(?<tool>[^"\r\n]+)"|`(?<tool>[^`\r\n]+)`|(?<tool>[^\s'"`]+))""";

    private static readonly Regex WindowsTool = CreateRegex(ToolPattern + @"[ \t]+" + WindowsPhrase);
    private static readonly Regex PosixToolAfter = CreateRegex(PosixPhrase + @"[ \t]*:[ \t]*" + ToolPattern);
    private static readonly Regex PosixToolBefore = CreateRegex(ToolPattern + @"[ \t]*:[ \t]*" + PosixPhrase);
    private static readonly Regex LinkerTool = CreateRegex(
        @"\blinker[ \t]+" + ToolPattern + @"[ \t]+not found\b|" +
        @"(?<![\w.-])(?<tool>link\.exe|lld-link(?:\.exe)?|rust-lld|ld\.lld)[ \t]+not found\b");
    private static readonly Regex ToolchainTool = CreateRegex(ToolchainPhrase + @"[ \t]+" + ToolPattern);

    internal static AcceptanceCheckResult Attach(AcceptanceCheckResult check)
    {
        if (check.Passed || !string.IsNullOrWhiteSpace(check.FailureClassification) ||
            check.FailureCauseEvidence is not null ||
            check.TestResultPaths is { Count: > 0 } || check.FailingTestIdentities is { Count: > 0 })
        {
            return check;
        }

        var output = string.Join(Environment.NewLine, check.OutputTail, check.ProcessStderr);
        SignatureMatch? matched;
        try
        {
            matched = MatchSignature(output, check.ExitCode);
        }
        catch (RegexMatchTimeoutException)
        {
            return check;
        }

        if (matched is null) return check;

        const string classification = AcceptanceFailureClassifications.MissingTool;
        var evidence = $"check={JsonSerializer.Serialize(check.Name)}; failureClassification={JsonSerializer.Serialize(classification)}" +
            $"; signature={matched.Signature}; source={matched.Source}; tool={matched.Tool ?? "unknown"}";
        return check with
        {
            FailureClassification = classification,
            FailureCauseEvidence = new AcceptanceFailureCauseEvidence(
                AcceptanceFailureCause.EnvironmentalApparatus, evidence, check.Name, classification)
        };
    }

    private static SignatureMatch? MatchSignature(string output, int? exitCode)
    {
        var windowsOutput = output.Contains(WindowsPhrase, StringComparison.OrdinalIgnoreCase);
        var posixOutput = output.Contains(PosixPhrase, StringComparison.OrdinalIgnoreCase);
        var linker = ExtractTool(LinkerTool, output);
        var toolchain = output.Contains(ToolchainPhrase, StringComparison.OrdinalIgnoreCase) &&
            output.Contains("no default is configured", StringComparison.OrdinalIgnoreCase);
        SignatureMatch?[] matches =
        [
            windowsOutput || exitCode == 9009
                ? new("windows-command-not-recognized", windowsOutput ? ExtractTool(WindowsTool, output) : null,
                    windowsOutput ? "output" : "exit-code") : null,
            posixOutput || exitCode == 127
                ? new("posix-command-not-found", posixOutput
                    ? ExtractTool(PosixToolAfter, output) ?? ExtractTool(PosixToolBefore, output) : null,
                    posixOutput ? "output" : "exit-code") : null,
            linker is not null ? new("linker-not-found", linker, "output") : null,
            toolchain ? new("toolchain-default-unconfigured", ExtractTool(ToolchainTool, output), "output") : null
        ];

        // Keep a diagnostic's tool identity even when an earlier family matched only its exit code.
        return matches.FirstOrDefault(match => match is { Source: "output", Tool: not null }) ??
            matches.FirstOrDefault(match => match is not null);
    }

    private static string? ExtractTool(Regex pattern, string output)
    {
        var match = pattern.Match(output);
        var tool = match.Success ? match.Groups["tool"].Value.Trim() : null;
        return string.IsNullOrEmpty(tool) ? null : tool;
    }

    private static Regex CreateRegex(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private sealed record SignatureMatch(string Signature, string? Tool, string Source);
}
