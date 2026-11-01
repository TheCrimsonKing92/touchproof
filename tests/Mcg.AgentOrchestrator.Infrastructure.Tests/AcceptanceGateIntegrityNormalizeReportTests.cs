using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every case owns its tree, fake labeler and async-local console capture.
public sealed class AcceptanceGateIntegrityNormalizeReportTests
{
    [Fact]
    public void Normalize_PrunedTree_ReportsVisitedEntriesAndSuccessfulWrites()
    {
        using var tree = new TempTree();
        tree.AddFile(".git", "ignored");
        tree.AddFile(".mcg-sandbox", "private");
        tree.AddFile("readme.txt");
        tree.AddFile("src", "code.cs");
        tree.AddFile("src", "nested", "artifact.dll");
        var labeler = new RecordingLabeler();

        var output = AsyncLocalConsoleRouter.Capture(() =>
            AcceptanceGateIntegrityNormalizer.Normalize(tree.Root, labeler));

        var lines = Lines(output);
        var summary = Assert.Single(lines, line => line.StartsWith("INTEGRITY_NORMALIZE "));
        Assert.Matches(@"^INTEGRITY_NORMALIZE elapsed_ms=\d+ entries=6 relabeled=6 skipped=2$", summary);
        Assert.Equal(summary, lines[^1]);
        Assert.Equal(6, labeler.Calls.Count);
        Assert.All(labeler.Calls, call => Assert.False(call.Recursive));
        Assert.DoesNotContain(labeler.Calls, call => call.Path.Contains(".git") ||
            call.Path.Contains(".mcg-sandbox"));

        Assert.Equal(6, lines.Length); // Root timing, four top-level timings, summary.
        AssertEntry(lines, ".", entries: 1, relabeled: 1, skipped: false);
        AssertEntry(lines, ".git", entries: 0, relabeled: 0, skipped: true);
        AssertEntry(lines, ".mcg-sandbox", entries: 0, relabeled: 0, skipped: true);
        AssertEntry(lines, "readme.txt", entries: 1, relabeled: 1, skipped: false);
        AssertEntry(lines, "src", entries: 4, relabeled: 4, skipped: false);
    }

    [Fact]
    public void Normalize_NestedMetadataNames_CountsThemAsOrdinaryEntries()
    {
        using var tree = new TempTree();
        tree.AddFile("src", ".git", "object");
        tree.AddFile("src", ".mcg-sandbox", "output");
        var labeler = new RecordingLabeler();

        var output = AsyncLocalConsoleRouter.Capture(() =>
            AcceptanceGateIntegrityNormalizer.Normalize(tree.Root, labeler));

        Assert.Matches(@"INTEGRITY_NORMALIZE elapsed_ms=\d+ entries=6 relabeled=6 skipped=0", output);
        Assert.Equal(6, labeler.Calls.Count);
        AssertEntry(Lines(output), "src", entries: 5, relabeled: 5, skipped: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Normalize_FailedPreparation_EmitsNoSuccessReport(bool failReadBack)
    {
        using var tree = new TempTree();
        var rejected = tree.AddFile("rejected.txt");
        var labeler = new RecordingLabeler(rejected, failReadBack);
        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var error = Assert.Throws<AcceptanceGateIntegrityPreparationException>(() =>
                AcceptanceGateIntegrityNormalizer.Normalize(tree.Root, labeler));
            Assert.Contains(rejected, error.Message);
        });

        Assert.Equal(2, labeler.Calls.Count);
        Assert.Empty(output);
    }

    [Fact]
    public void Normalize_SelectiveLabels_ReportsOnlyNecessaryWrites()
    {
        using var tree = new TempTree();
        tree.AddFile(".git", "ignored");
        tree.AddFile(".mcg-sandbox", "private");
        tree.AddFile("src", "nested", "output.txt");
        for (var index = 0; index < 40; index++) tree.AddFile($"file-{index}.txt");
        var nested = Path.Combine(tree.Root, "src", "nested");
        var labeler = new SelectiveLabeler(nested);

        var output = AsyncLocalConsoleRouter.Capture(() =>
            AcceptanceGateIntegrityNormalizer.Normalize(tree.Root, labeler));

        var lines = Lines(output);
        Assert.Matches(@"^INTEGRITY_NORMALIZE elapsed_ms=\d+ entries=44 relabeled=1 skipped=2$", lines[^1]);
        Assert.Equal(nested, Assert.Single(labeler.Calls));
        Assert.Equal(45, labeler.Queries.Count); // Every entry plus the successful write's readback.
        Assert.DoesNotContain(labeler.Queries, path => path.Contains(".git") || path.Contains(".mcg-sandbox"));
        AssertEntry(lines, ".", entries: 1, relabeled: 0, skipped: false);
        AssertEntry(lines, "src", entries: 3, relabeled: 1, skipped: false);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Normalize_SelectiveFailure_ThrowsWithoutSuccessOutput(bool failReadBack, bool queryError)
    {
        using var tree = new TempTree();
        var rejected = tree.AddFile("rejected.txt");
        var labeler = new SelectiveLabeler(rejected, failReadBack, queryError);

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var error = Assert.Throws<AcceptanceGateIntegrityPreparationException>(() =>
                AcceptanceGateIntegrityNormalizer.Normalize(tree.Root, labeler));
            Assert.Contains(rejected, error.Message);
        });

        Assert.Equal(rejected, Assert.Single(labeler.Calls));
        Assert.Empty(output);
    }

    [Fact]
    public void Normalize_UnwritableConsole_KeepsSuccessfulPreparation()
    {
        using var tree = new TempTree();
        tree.AddFile("file.txt");
        var labeler = new RecordingLabeler();
        using var writer = new FailingWriter();

        AsyncLocalConsoleRouter.CaptureTo(() =>
            AcceptanceGateIntegrityNormalizer.Normalize(tree.Root, labeler), writer);

        Assert.Equal(2, labeler.Calls.Count);
        Assert.Equal(3, writer.Attempts); // Both entry lines and the summary were attempted.
    }

    private static string[] Lines(string output) => output.Split(
        ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static void AssertEntry(string[] lines, string name, int entries, int relabeled, bool skipped)
    {
        var prefix = $"INTEGRITY_NORMALIZE_ENTRY name={JsonSerializer.Serialize(name)} ";
        var line = Assert.Single(lines, line => line.StartsWith(prefix, StringComparison.Ordinal));
        Assert.Matches(@"^elapsed_ms=\d+ entries=" + entries + " relabeled=" + relabeled +
            " skipped=" + (skipped ? "true" : "false") + "$", line[prefix.Length..]);
    }

    private sealed class TempTree : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("gate-report-").FullName;

        internal string AddFile(params string[] segments)
        {
            var path = Path.Combine([Root, .. segments]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class RecordingLabeler(string? rejected = null, bool failReadBack = false)
        : IWorkerIntegrityLabeler
    {
        internal List<(string Path, bool Recursive)> Calls { get; } = [];

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            Calls.Add((path, recursive));
            Assert.Equal(Directory.Exists(path) ? "(OI)(CI)M" : "M", level);
            return failReadBack || path != rejected;
        }

        public IntegrityLabelState Query(string path) => new(
            Exists: File.Exists(path) || Directory.Exists(path), Low: path == rejected,
            Inheritable: Directory.Exists(path), Medium: path != rejected);
    }

    private sealed class FailingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        internal int Attempts { get; private set; }

        public override void WriteLine(string? value)
        {
            Attempts++;
            throw new IOException("Test-owned diagnostic sink is unavailable.");
        }
    }

    private sealed class SelectiveLabeler(string target, bool? failReadBack = null, bool queryError = false)
        : IAcceptanceGateSelectiveIntegrityLabeler
    {
        internal List<string> Calls { get; } = [];
        internal List<string> Queries { get; } = [];

        public IntegrityLabelState Query(string path)
        {
            Queries.Add(path);
            if (path == target && (Calls.Count == 0 || failReadBack == true))
                return queryError
                    ? new(Exists: false, Low: false, Inheritable: false, NativeQueryError: 5)
                    : new(Exists: true, Low: true, Inheritable: Directory.Exists(path));
            return Directory.Exists(path)
                ? new(Exists: true, Low: false, Inheritable: true, Medium: true)
                : new(Exists: true, Low: false, Inheritable: false, Unlabeled: true);
        }

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            Calls.Add(path);
            Assert.Equal(target, path);
            Assert.False(recursive);
            return failReadBack is null or true;
        }
    }
}
