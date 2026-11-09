using Mcg.AgentOrchestrator.Infrastructure;
using System.Text;
using static WorkerDispatchTestsSeededRepositoryFactory;

// Stateless: each eligible read must establish positive evidence from the current ref files.
internal static class GitProbeRefFileFastPath
{
    internal static bool TryAnswer(string workingDirectory, IReadOnlyList<string> arguments,
        bool hasSeamOverrideOrHook, out GitProbeResult answer)
    {
        answer = null!;
        if (hasSeamOverrideOrHook || arguments.Count < 2 || arguments[0] != "rev-parse")
            return false;

        // RunGitProbe uses ArgumentList, so these are already the exact child tokens.
        var index = 1;
        while (index < arguments.Count && arguments[index] is "--verify" or "-q" or "--quiet")
            index++;
        if (index != arguments.Count - 1)
            return false;

        var revision = arguments[index];
        if (string.IsNullOrWhiteSpace(revision) || revision[0] == '-' || revision.Contains('\0') ||
            !GitRefFileResolver.TryResolve(workingDirectory, revision, out var id, ReadEnvironment))
            return false;

        var output = id + "\n";
        answer = new GitProbeResult(
            "git " + string.Join(' ', arguments) + " [resolved from ref files]",
            // GitProbeResult.Succeeded requires this legacy flag; no child identity is invented.
            ProcessStarted: true, ExitCode: 0, StandardOutput: output, StandardError: string.Empty,
            DrainTimedOut: false, TimedOut: false,
            EnvironmentContract: "resolved=ref-files; repositorySelection=unset",
            Arguments: arguments, RepositoryDirectory: Path.GetFullPath(workingDirectory),
            StandardOutputByteCount: Encoding.UTF8.GetByteCount(output),
            Classification: GitProbeClassification.Success, Observations: new(new(), new(), new()));
        InfrastructureTestSupport.RecordGitProbeRefFileAnswer();
        return true;
    }

    private static string? ReadEnvironment(string name) => name.ToUpperInvariant() switch
    {
        "GIT_DIR" or "GIT_WORK_TREE" or "GIT_INDEX_FILE" or "GIT_OBJECT_DIRECTORY" or
            "GIT_ALTERNATE_OBJECT_DIRECTORIES" or "GIT_COMMON_DIR" => null,
        _ => Environment.GetEnvironmentVariable(name)
    };
}
