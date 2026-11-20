// Shared fixture directory ownership for snapshot tests. Each instance is independent.
internal sealed class DecisionSnapshotTestDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("decision-snapshot-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
