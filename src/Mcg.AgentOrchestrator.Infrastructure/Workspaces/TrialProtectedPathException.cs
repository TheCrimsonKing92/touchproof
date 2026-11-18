namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class TrialProtectedPathException : IOException
{
    public TrialProtectedPathException(string path, Exception innerException)
        : base(
            $"Protected trial path '{path}' " +
            (innerException is FileNotFoundException or DirectoryNotFoundException
                ? "vanished during capture. "
                : "was unreadable during capture. ") +
            "Point the trial at a clean exact-commit checkout or stop the writer and retry.",
            innerException)
    {
    }
}
