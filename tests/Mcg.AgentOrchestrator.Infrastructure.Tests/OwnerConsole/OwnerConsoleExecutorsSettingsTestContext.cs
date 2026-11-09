using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Each consumer owns its executor store and intent inbox in a disposable workspace.
internal sealed class OwnerConsoleExecutorsSettingsTestContext : IDisposable
{
    internal static readonly DateTimeOffset At = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = SharedTestSupport.CreateTempDirectory();
    internal OrchestratorWorkspace Workspace { get; }
    internal SqliteOperatorIntentStore Intents { get; }
    internal string Path => RemoteLaneExecutorConfiguration.ResolveStorePath(Workspace.ExecutionDirectory);
    internal string Folder => System.IO.Path.GetDirectoryName(Path)!;

    internal OwnerConsoleExecutorsSettingsTestContext()
    {
        Workspace = OrchestratorWorkspace.ForDirectory(_root);
        Intents = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
        Directory.CreateDirectory(Folder);
        Write("""{"executors":[{"id":"mcg-exec-1","slots":3},{"id":"mcg-exec-2","slots":2}],"lanes":[]}""");
    }

    internal void Write(string text) => File.WriteAllText(Path, text);
    internal byte[]? FileBytes() => File.Exists(Path) ? File.ReadAllBytes(Path) : null;
    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
