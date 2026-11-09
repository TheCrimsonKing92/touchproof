using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class ImpactReplaySelectionFixture : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("impact-selection-");
    private readonly List<string> _files = [];
    internal string Root => _directory.FullName;

    internal void Add(string name, string text)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, text);
        _files.Add(path);
    }

    internal ImpactReplayTestSourceIndex Sources => new(_files);
    internal IReadOnlyList<AcceptanceTestClassDescriptor> Classes => AcceptanceTestClassSourceScanner.Scan(Root, _files.ToArray());
    public void Dispose() => _directory.Delete(recursive: true);
}
