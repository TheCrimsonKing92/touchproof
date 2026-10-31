using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns its filesystem tree and labeler; the collection covers junction creation.
[Collection(TestCollections.ProcessSpawning)]
public sealed class IcaclsIntegrityLabelerTestsAcceptanceGateReparsePointTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Normalize_Junctions_SkipsLinksAtEveryDepth(bool nested, bool dangling)
    {
        var root = Directory.CreateTempSubdirectory("gate-reparse-").FullName;
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(workspace, "output")).FullName;
        var binaryDirectory = Directory.CreateDirectory(Path.Combine(output, "bin")).FullName;
        var executable = Path.Combine(binaryDirectory, "probe.exe");
        File.WriteAllText(executable, "existing Low output");
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        var outsideFile = Path.Combine(outside, "untouched.exe");
        File.WriteAllText(outsideFile, "outside output");
        var link = Path.Combine(nested ? output : workspace, "link");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var created = await WorkerProcessRunner.RunBufferedAsync(new(
                    $"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{outside.Replace("'", "''")}' | Out-Null",
                    root, TimeSpan.FromSeconds(30)), CancellationToken.None);
                Assert.False(created.TimedOut);
                Assert.Equal(0, created.ExitCode);
            }
            else
            {
                Directory.CreateSymbolicLink(link, outside);
            }

            Assert.True((new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0);
            if (dangling)
            {
                File.Delete(outsideFile);
                Directory.Delete(outside);
            }
            Assert.Equal(!dangling, Directory.Exists(outside));
            Assert.Equal(outside, new DirectoryInfo(link).LinkTarget);
            var labeler = new RecordingLabeler();

            AcceptanceGateIntegrityNormalizer.Normalize(workspace, labeler);

            // No recursive call can delegate a hidden traversal through a nested link.
            Assert.All(labeler.Calls, call => Assert.False(call.Recursive));
            Assert.Equal(4, labeler.Calls.Count);
            foreach (var path in new[] { workspace, output, binaryDirectory, executable })
            {
                var call = Assert.Single(labeler.Calls, call => call.Path == path);
                Assert.Equal(path == executable ? "M" : "(OI)(CI)M", call.Level);
            }
            Assert.DoesNotContain(labeler.Calls, call => call.Path == link ||
                call.Path.StartsWith(outside, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            // Remove the link itself, including a dangling one, before recursive cleanup.
            if (new DirectoryInfo(link).LinkTarget is not null)
                Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingLabeler : IWorkerIntegrityLabeler
    {
        internal List<(string Path, string Level, bool Recursive)> Calls { get; } = [];

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            Calls.Add((path, level, recursive));
            return true;
        }

        public IntegrityLabelState Query(string path) => new(
            Exists: File.Exists(path) || Directory.Exists(path), Low: false,
            Inheritable: Directory.Exists(path), Medium: true);
    }
}
