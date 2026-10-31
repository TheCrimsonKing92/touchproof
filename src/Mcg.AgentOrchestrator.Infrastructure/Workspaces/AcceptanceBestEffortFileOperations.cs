namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceBestEffortFileOperations
{
    internal static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best effort; lives under the temp dir */ }
    }

    internal static long TryGetFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0L; }
        catch { return 0L; }
    }
}
