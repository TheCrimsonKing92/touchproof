using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class VstestCompleteOutputFixture
{
    internal static GoalAcceptanceVerifier Verifier(
        Func<string[], string, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> runner) =>
        Verifier(new GoalAcceptanceVerifierTestOverrides(), runner);

    internal static GoalAcceptanceVerifier Verifier(
        GoalAcceptanceVerifierTestOverrides overrides,
        Func<string[], string, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> runner,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? leaseSleep = null) =>
        new(overrides, async (args, workspace, token) =>
        {
            var result = await runner(args, workspace, token);
            if (result.ExitCode == 0) WriteForBuild(args, workspace);
            return result;
        }, timeProvider, leaseSleep);

    internal static GoalAcceptanceVerifier Verifier(
        GoalAcceptanceVerifierTestOverrides overrides,
        Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> runner,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? leaseSleep = null) =>
        new(overrides, async (args, workspace, timeout, token) =>
        {
            var result = await runner(args, workspace, timeout, token);
            if (result.ExitCode == 0) WriteForBuild(args, workspace);
            return result;
        }, timeProvider, leaseSleep);

    internal static string Create(string artifactsPath, string assemblyName, string pivot = "debug")
    {
        var output = Path.Combine(artifactsPath, "bin", assemblyName, pivot);
        Directory.CreateDirectory(output);
        foreach (var file in new[] { assemblyName + ".dll", assemblyName + ".runtimeconfig.json", "testhost.dll" })
            if (!File.Exists(Path.Combine(output, file))) File.WriteAllText(Path.Combine(output, file), "fixture");
        return output;
    }

    internal static void WriteForBuild(string[] args, string workspace)
    {
        if (args.Length < 2 || args[0] != "dotnet" || args[1] != "build") return;
        var artifactsIndex = Array.IndexOf(args, "--artifacts-path");
        if (artifactsIndex < 0) return;
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace, "config", "acceptance-manifest.json")));
        foreach (var check in manifest.RootElement.GetProperty("checks").EnumerateArray())
            if ((!check.TryGetProperty("runner", out var runner) || runner.GetString() != "mtp") &&
                check.TryGetProperty("project", out var project) &&
                project.GetString() is { } path && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                Create(args[artifactsIndex + 1], Path.GetFileNameWithoutExtension(path));
    }
}
