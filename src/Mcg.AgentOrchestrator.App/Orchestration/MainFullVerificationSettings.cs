using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MainFullVerificationSettings
{
    internal const string IntervalKey = "mainFullVerificationIntervalSeconds";
    internal const int DefaultIntervalSeconds = 6 * 60 * 60;

    internal static int ReadIntervalSeconds(string? path, Action<string> progress)
    {
        if (path is null) return DefaultIntervalSeconds;
        try
        {
            using var policy = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
                { CommentHandling = JsonCommentHandling.Skip });
            if (policy.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (!policy.RootElement.TryGetProperty(IntervalKey, out var value)) return DefaultIntervalSeconds;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var seconds) && seconds > 0)
                    return seconds;
            }
        }
        catch (FileNotFoundException) { return DefaultIntervalSeconds; }
        catch (DirectoryNotFoundException) { return DefaultIntervalSeconds; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }

        progress($"MAIN_FULL_VERIFICATION result=policy-warning setting={IntervalKey} defaultSeconds={DefaultIntervalSeconds}");
        return DefaultIntervalSeconds;
    }
}
