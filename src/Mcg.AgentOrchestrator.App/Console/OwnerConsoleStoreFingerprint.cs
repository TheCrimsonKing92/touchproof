using System.Globalization;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleStoreFingerprint
{
    internal static Func<string?> For(string statePath, string collaborationPath) => () =>
    {
        try
        {
            return string.Join("|", new[] { statePath, statePath + "-wal", collaborationPath, collaborationPath + "-wal" }
                .Select(path =>
                {
                    var file = new FileInfo(path);
                    return file.Exists ? string.Create(CultureInfo.InvariantCulture,
                        $"{file.Length}:{file.LastWriteTimeUtc.Ticks}") : "missing";
                }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    };
}
