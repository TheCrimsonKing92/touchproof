using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Only the home supervisor publishes authority. Readers never infer adoption from a staged folder.
internal sealed record HomeBuildAdoptionRecord(
    int SchemaVersion, string HomeSourceRoot, long Sequence, string AdoptedCommit,
    string RunDirectory, string AppDllPath, string Status)
{
    internal static string RecordPath(string homeRoot) => Path.Combine(
        OrchestratorWorkspace.ForDirectory(homeRoot).OrchestratorDirectory, "continuity", "home-build-adoption.json");

    internal static HomeBuildAdoptionRecord? Read(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<HomeBuildAdoptionRecord>(File.ReadAllText(path)) : null;

    internal static void Write(string path, HomeBuildAdoptionRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(record));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    internal static string LauncherPointerPath(string recordPath) => Path.Combine(
        Path.GetDirectoryName(recordPath)!, "home-build-adoption.cmd.txt");

    internal static void WriteLauncherPointer(string recordPath, HomeBuildAdoptionRecord record)
    {
        var path = LauncherPointerPath(recordPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary,
                $"{record.AdoptedCommit.ToLowerInvariant()}\r\n{Path.GetFullPath(record.AppDllPath)}\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void DeleteLauncherPointer(string recordPath) => File.Delete(LauncherPointerPath(recordPath));

    // Historical successful identities provide the sequence of an already-running build after restart.
    internal static string BuildIdentityPath(string recordPath, string commit) => Path.Combine(
        Path.GetDirectoryName(recordPath)!, "home-build-identities", commit + ".json");
}
