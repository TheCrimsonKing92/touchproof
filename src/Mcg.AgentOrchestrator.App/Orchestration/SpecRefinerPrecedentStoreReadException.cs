namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class SpecRefinerPrecedentStoreReadException : InvalidOperationException
{
    internal SpecRefinerPrecedentStoreReadException(string filePath, Exception? innerException)
        : base($"Could not read precedent snapshot '{filePath}'.", innerException)
    {
        FilePath = filePath;
    }

    internal string FilePath { get; }
}
