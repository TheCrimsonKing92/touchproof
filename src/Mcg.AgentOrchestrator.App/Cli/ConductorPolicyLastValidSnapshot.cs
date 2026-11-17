using System.Globalization;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Cli;

// Recovery state contains only a validated file policy, never invocation overrides or fallback policies.
internal static class ConductorPolicyLastValidSnapshot
{
    internal const string FileName = "conductor-policy.json.last-valid.json";

    internal static string PathFor(string orchestratorDirectory) =>
        Path.Combine(Path.GetFullPath(orchestratorDirectory), FileName);

    private static string Serialize(ConductorAutonomyPolicy policy)
    {
        var ordered = policy with
        {
            TransitionMap = policy.TransitionMap.OrderBy(pair => pair.Key)
                .ToDictionary(pair => pair.Key, pair => pair.Value)
        };
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var json = ordered.ToJson();
            ConductorAutonomyPolicy.ParseJson(json);
            return json;
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    internal static string? Write(string orchestratorDirectory, ConductorAutonomyPolicy policy)
    {
        var path = PathFor(orchestratorDirectory);
        string? temporaryPath = null;
        try
        {
            var json = Serialize(policy);
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), json, StringComparison.Ordinal))
                return null;

            temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"POLICY_SNAPSHOT_WRITE_FAILED path={path} reason={ex.Message.ReplaceLineEndings(" ")}";
        }
        finally
        {
            if (temporaryPath is not null)
                try { File.Delete(temporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static ConductorAutonomyPolicy? TryRead(string orchestratorDirectory, out string? failure)
    {
        var path = PathFor(orchestratorDirectory);
        try
        {
            var policy = ConductorAutonomyPolicy.ParseJson(File.ReadAllText(path), path);
            failure = null;
            return policy;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            failure = ex.Message.ReplaceLineEndings(" ");
            return null;
        }
    }
}
