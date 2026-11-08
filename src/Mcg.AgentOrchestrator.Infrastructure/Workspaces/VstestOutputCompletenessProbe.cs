namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class VstestOutputCompletenessProbe
{
    internal static AcceptanceCheckResult? BeforeNoBuildRun(
        GoalAcceptanceVerifier.AcceptanceManifestCheck check, string[] testArguments,
        DotnetBuildEnvironment? buildEnvironment, AcceptanceCheckResult buildResult, bool captureLimited)
    {
        if (!buildResult.Passed)
            return AcceptanceDotnetBuildPhase.FailureResult(check, buildResult, captureLimited);

        var project = !string.IsNullOrWhiteSpace(check.Project) ? check.Project
            : testArguments.Length > 2 && !testArguments[2].StartsWith('-') ? testArguments[2] : null;
        // A solution/directory target does not identify an individual test assembly.
        if (project is null || !(Path.GetExtension(project).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(project).Equals(".fsproj", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(project).Equals(".vbproj", StringComparison.OrdinalIgnoreCase)))
            return null;

        return ProbeProject(check, project, testArguments, buildEnvironment?.ArtifactsPath ?? buildResult.ArtifactsPath);
    }

    internal static VstestOutputProbeOutcome Probe(string outputDirectory, string assemblyName)
    {
        var resolved = Path.GetFullPath(outputDirectory);
        return new(resolved, RequiredFiles(assemblyName)
            .Where(file => !File.Exists(Path.Combine(resolved, file))).ToArray());
    }

    // The verifier supplies the same artifacts root and target used by its no-build invocation.
    internal static AcceptanceCheckResult? ProbeProject(
        GoalAcceptanceVerifier.AcceptanceManifestCheck check,
        string project,
        IReadOnlyList<string> testArguments,
        string? artifactsPath)
    {
        var assemblyName = Path.GetFileNameWithoutExtension(project);
        var projectRoot = artifactsPath is null
            ? $"unresolved artifacts path/bin/{assemblyName}"
            : Path.GetFullPath(Path.Combine(artifactsPath, "bin", assemblyName));
        IReadOnlyList<string> directories = artifactsPath is null ? []
            : ResolveOutputDirectories(artifactsPath, project, testArguments);
        var outcomes = directories.Count == 0
            ? new[] { new VstestOutputProbeOutcome(projectRoot, RequiredFiles(assemblyName)) }
            : directories.Select(directory => Probe(directory, assemblyName)).ToArray();
        var incomplete = outcomes.Where(outcome => !outcome.IsComplete).ToArray();
        if (incomplete.Length == 0) return null;

        return incomplete[0].ToFailedCheckResult(check, artifactsPath) with
        {
            OutputTail = string.Join("; ", incomplete.Select(outcome => outcome.Message)),
            ResultSummary = string.Join("; ", incomplete.Select(outcome => outcome.Message))
        };
    }

    internal static IReadOnlyList<string> ResolveOutputDirectories(
        string artifactsPath, string project, IReadOnlyList<string> arguments)
    {
        var assemblyName = Path.GetFileNameWithoutExtension(project);
        var projectRoot = Path.GetFullPath(Path.Combine(artifactsPath, "bin", assemblyName));
        if (!Directory.Exists(projectRoot)) return [];

        var configuration = OptionValue(arguments, "--configuration", "-c");
        var framework = OptionValue(arguments, "--framework", "-f");
        return Directory.EnumerateDirectories(projectRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(directory =>
            {
                var pivot = Path.GetFileName(directory);
                var segments = pivot.Split('_');
                return (configuration is null || segments[0].Equals(configuration, StringComparison.OrdinalIgnoreCase)) &&
                    (framework is null || segments.Length == 1 ||
                     segments.Skip(1).Contains(framework, StringComparer.OrdinalIgnoreCase)) &&
                    File.Exists(Path.Combine(directory, assemblyName + ".dll"));
            })
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? OptionValue(IReadOnlyList<string> arguments, string longName, string shortName)
    {
        string? value = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if ((argument == longName || argument == shortName) && index + 1 < arguments.Count)
                value = arguments[++index];
            else if (argument.StartsWith(longName + "=", StringComparison.Ordinal) ||
                     argument.StartsWith(shortName + "=", StringComparison.Ordinal))
                value = argument[(argument.IndexOf('=') + 1)..];
        }
        return value;
    }

    private static string[] RequiredFiles(string assemblyName) =>
        [assemblyName + ".runtimeconfig.json", "testhost.dll"];
}

internal sealed record VstestOutputProbeOutcome(string OutputDirectory, IReadOnlyList<string> MissingFiles)
{
    internal bool IsComplete => MissingFiles.Count == 0;
    internal AcceptanceShardCompletionDecision? Failure => IsComplete ? null
        : new(false, AcceptanceShardCompletionPredicates.IncompleteVstestOutput, false, null, null, null, "not-run");
    internal string? Message => IsComplete ? null
        : $"Incomplete VSTest output in '{OutputDirectory}': missing {string.Join(", ", MissingFiles)}.";

    internal AcceptanceCheckResult ToFailedCheckResult(
        GoalAcceptanceVerifier.AcceptanceManifestCheck check, string? artifactsPath) =>
        new(check.Name, false, null, Message, artifactsPath, ResultSummary: Message, Advisory: check.Advisory,
            FailureClassification: (Failure ?? throw new InvalidOperationException(
                "Complete VSTest output cannot produce an incomplete-output failure.")).FailedPredicate,
            CompletionDecision: Failure);
}
