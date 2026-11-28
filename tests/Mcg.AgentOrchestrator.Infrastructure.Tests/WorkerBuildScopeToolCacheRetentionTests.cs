using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Microsoft.Win32.SafeHandles;

// Parallel-safe. Each case owns its filesystem paths, and the CLI gets a child-only override.
public sealed class WorkerBuildScopeToolCacheRetentionTests
{
    private const string Pattern = "worker-build-scope-tool-cache";
    private const long Budget = 4L * 1024 * 1024 * 1024;
    private static readonly DateTimeOffset Now = RetentionReclaimFixture.Now;
    private static readonly OrchestratorArtifactRegistry.Entry RegisteredEntry =
        Assert.Single(OrchestratorArtifactRegistry.Entries, entry => entry.Pattern == Pattern);

    [Fact]
    public async Task StorageHealth_ExternalToolCacheRoot_ReportsOwnerBudgetBytesFilesAndStatus()
    {
        using var fixture = new RetentionReclaimFixture();
        var cache = fixture.OrchestratorDirectory + "-scope-cache";
        WriteEntry(cache, Digest(1), Now, Budget);
        WriteEntry(cache, Digest(2), Now, 17);

        var json = await RunStorageHealth(fixture, cache, "--json");
        using var document = JsonDocument.Parse(json);
        var row = Assert.Single(document.RootElement.GetProperty("classes").EnumerateArray(),
            entry => entry.GetProperty("pattern").GetString() == Pattern);
        Assert.Equal("WorkerBuildScopeToolCache", row.GetProperty("owner").GetString());
        Assert.Equal("WorkerBuildScopeToolCacheRetention", row.GetProperty("retention").GetString());
        Assert.Equal(Budget, row.GetProperty("budget_bytes").GetInt64());
        Assert.Equal(Budget + 17, row.GetProperty("bytes").GetInt64());
        Assert.Equal(2, row.GetProperty("files").GetInt64());
        Assert.Equal("over", row.GetProperty("status").GetString());

        var table = await RunStorageHealth(fixture, cache);
        var fields = Assert.Single(table.Split(Environment.NewLine), line => line.StartsWith($"class\t{Pattern}\t", StringComparison.Ordinal))
            .Split('\t');
        Assert.Contains("owner=WorkerBuildScopeToolCache", fields);
        Assert.Contains("budget_bytes=4294967296", fields);
        Assert.Contains("bytes=4294967313", fields);
        Assert.Contains("files=2", fields);
        Assert.Contains("status=over", fields);
    }

    [Fact]
    public void Identity_OverrideRelativeAndLocalAppDataFallback_MatchScriptRule()
    {
        using var fixture = new RetentionReclaimFixture();
        var cwd = fixture.ExecutionDirectory;
        var local = Path.Combine(cwd, "local");
        var fallback = Path.Combine(cwd, "fallback");
        var suffix = Path.Combine("Mcg.AgentOrchestrator", "tool-cache", "worker-build-scope");
        string UnexpectedFallback() => throw new InvalidOperationException("Fallback should not run.");

        Assert.Equal(Path.Combine(cwd, "override"), WorkerBuildScopeToolCacheIdentity.Resolve(
            Path.Combine("relative", "..", "override"), local, UnexpectedFallback, cwd));
        Assert.Equal(fallback, WorkerBuildScopeToolCacheIdentity.Resolve(fallback, local, UnexpectedFallback, cwd));
        Assert.Equal(Path.Combine(local, suffix), WorkerBuildScopeToolCacheIdentity.Resolve(" ", local, UnexpectedFallback, cwd));
        Assert.Equal(Path.Combine(fallback, suffix), WorkerBuildScopeToolCacheIdentity.Resolve(null, " ", () => fallback, cwd));
        Assert.Throws<InvalidOperationException>(() => WorkerBuildScopeToolCacheIdentity.Resolve(null, null, () => "", cwd));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public void Sweep_OverBudget_KeepsNewestEightAndEntriesWithinTwoDays(int currentCount)
    {
        using var fixture = new RetentionReclaimFixture();
        var cache = Path.Combine(fixture.ExecutionDirectory, "cache");
        var reproducibleOrder = Enumerable.Range(1, 14)
            .Select(index => WriteEntry(cache, Digest(index),
                index <= currentCount ? Now.AddDays(-2) : Now.AddDays(-3), index == 1 ? Budget : index))
            .ToArray();
        Assert.Equal("over", Measure(fixture, cache).Status);

        var decisions = Sweep(cache);

        foreach (var entry in reproducibleOrder.Take(Math.Max(8, currentCount)))
            Assert.True(File.Exists(Path.Combine(entry, "payload.bin")), entry);
        Assert.Equal(reproducibleOrder.Skip(Math.Max(8, currentCount)),
            decisions.Select(decision => decision.Path));
        Assert.All(decisions, decision => Assert.Equal(EvidenceRetentionAction.Deleted, decision.Action));
        Assert.Equal("over", Measure(fixture, cache).Status);
    }

    [Fact]
    public void Sweep_EntryWithOpenHandle_KeepsEntryWholeAndRecordsHeld()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows directory-rename sharing semantics.");
        using var fixture = new RetentionReclaimFixture();
        var cache = Path.Combine(fixture.ExecutionDirectory, "cache");
        SeedNewest(cache);
        var entry = WriteEntry(cache, Digest(9), Now.AddDays(-10), 7, nestedBytes: 9);
        var payload = Path.Combine(entry, "payload.bin");
        var nested = Path.Combine(entry, "nested", "payload.bin");
        using var handle = File.Open(nested, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var probe = Path.Combine(cache, ".rename-probe");
        var blocked = Record.Exception(() => Directory.Move(entry, probe));
        Assert.True(blocked is IOException or UnauthorizedAccessException, $"Expected a blocked rename, got {blocked}.");

        var held = Assert.Single(Sweep(cache));

        Assert.Equal(entry, held.Path);
        Assert.Equal(EvidenceArtifactFamily.WorkerBuildScopeToolCaches, held.Family);
        Assert.Equal(EvidenceRetentionAction.DeferredLocked, held.Action);
        Assert.Equal("worker-build-scope-tool-cache-held", held.Reason);
        Assert.NotNull(held.FailureExceptionType);
        Assert.Equal(0, held.BytesReclaimed);
        Assert.Equal(new byte[7], File.ReadAllBytes(payload));
        Assert.Equal(new byte[9], File.ReadAllBytes(nested));
        Assert.Empty(Directory.EnumerateDirectories(cache, ".sweep-retired-*"));
    }

    [Fact]
    public void Sweep_StaleEntries_ReclaimsBytesAndLeavesForeignNamesUntouched()
    {
        using var fixture = new RetentionReclaimFixture();
        var cache = Path.Combine(fixture.ExecutionDirectory, "cache");
        SeedNewest(cache);
        var old = Now.AddDays(-10);
        var stale = WriteEntry(cache, new string('A', 64), old, 31, nestedBytes: 11);
        var second = WriteEntry(cache, Digest(10), old, 19);
        var foreign = new[] { new string('b', 63), new string('b', 65), new string('g', 64), ".stage-x", ".sweep-retired-previous" }
            .Select(name => WriteEntry(cache, name, old, 5))
            .ToArray();
        var looseFile = Path.Combine(cache, Digest(11));
        File.WriteAllText(looseFile, "loose file");
        var outside = WriteEntry(fixture.ExecutionDirectory, "outside", old, 113);
        var junction = Path.Combine(cache, Digest(12));
        var nestedLink = Path.Combine(stale, "link");
        try
        {
            CreateDirectoryLink(junction, outside, fixture.ExecutionDirectory);
            CreateDirectoryLink(nestedLink, outside, fixture.ExecutionDirectory);
            Directory.SetLastWriteTimeUtc(stale, old.UtcDateTime);

            var decisions = Sweep(cache);

            Assert.Equal(2, decisions.Count);
            AssertReclaimed(Assert.Single(decisions, decision => decision.Path == stale), 42);
            AssertReclaimed(Assert.Single(decisions, decision => decision.Path == second), 19);
            Assert.Equal(61, decisions.Sum(decision => decision.BytesReclaimed));
            Assert.All(foreign, path => Assert.Equal(new byte[5], File.ReadAllBytes(Path.Combine(path, "payload.bin"))));
            Assert.Equal("loose file", File.ReadAllText(looseFile));
            Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(new byte[113], File.ReadAllBytes(Path.Combine(junction, "payload.bin")));
            Assert.Equal(new byte[113], File.ReadAllBytes(Path.Combine(outside, "payload.bin")));
            Assert.Equal(new[] { Path.Combine(cache, ".sweep-retired-previous") }, Directory.GetDirectories(cache, ".sweep-retired-*"));
        }
        finally
        {
            // Unlink the fixture junctions before the fixture recursively deletes its root.
            foreach (var link in new[] { junction, nestedLink })
                if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(link, recursive: false);
        }
    }

    [Fact]
    public void StorageRetentionPass_OverBudgetToolCache_ReportsOkAfterward()
    {
        using var fixture = new RetentionReclaimFixture();
        var cache = Path.Combine(fixture.ExecutionDirectory, "cache");
        var old = Now.AddDays(-10);
        SeedNewest(cache, Now.AddDays(-5));
        WriteEntry(cache, Digest(9), old, Budget);
        WriteEntry(cache, Digest(10), old, 19);
        var before = Measure(fixture, cache);
        Assert.Equal("over", before.Status);
        Assert.Equal(Budget + 27, before.Bytes);

        var result = StorageRetentionMaintenance.Run(fixture.LogDirectory, fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory, [], Now, mtpResultsRoot: Path.Combine(fixture.ExecutionDirectory, "empty-mtp"),
            workerBuildScopeToolCacheRoot: cache);

        Assert.False(result.Failed);
        var decisions = result.Decisions.Where(decision => decision.Family == EvidenceArtifactFamily.WorkerBuildScopeToolCaches).ToArray();
        Assert.Equal(2, decisions.Length);
        Assert.All(decisions, decision => Assert.Equal(EvidenceRetentionAction.Deleted, decision.Action));
        Assert.Equal(Budget + 19, decisions.Sum(decision => decision.BytesReclaimed));
        var after = Measure(fixture, cache);
        Assert.Equal("ok", after.Status);
        Assert.Equal(Budget, after.BudgetBytes);
        Assert.Equal(8, after.Bytes);
        Assert.Equal(8, after.Files);
    }

    private static StorageHealthMeasurement.ClassRow Measure(RetentionReclaimFixture fixture, string cache) =>
        Assert.Single(StorageHealthMeasurement.Build(fixture.OrchestratorDirectory,
            [RegisteredEntry with { ExternalRoot = () => cache }]).Classes);

    private static List<EvidenceRetentionDecision> Sweep(string cache)
    {
        var decisions = new List<EvidenceRetentionDecision>();
        WorkerBuildScopeToolCacheRetention.Sweep(cache, Now, decisions);
        return decisions;
    }

    private static void AssertReclaimed(EvidenceRetentionDecision decision, long bytes)
    {
        Assert.Equal(EvidenceArtifactFamily.WorkerBuildScopeToolCaches, decision.Family);
        Assert.Equal(EvidenceRetentionAction.Deleted, decision.Action);
        Assert.Equal(bytes, decision.BytesReclaimed);
        Assert.False(Directory.Exists(decision.Path), decision.Path);
    }

    private static void SeedNewest(string cache, DateTimeOffset? writeTime = null)
    {
        for (var index = 1; index <= 8; index++)
            WriteEntry(cache, Digest(index), writeTime ?? Now, 1);
    }

    private static string Digest(int index) => index.ToString("x64");

    private static string WriteEntry(string cache, string name, DateTimeOffset writeTime, long bytes, int nestedBytes = 0)
    {
        var entry = Path.Combine(cache, name);
        Directory.CreateDirectory(entry);
        using (var payload = File.Create(Path.Combine(entry, "payload.bin")))
        {
            if (OperatingSystem.IsWindows() && bytes >= Budget)
            {
                const uint setSparse = 0x000900C4;
                Assert.True(DeviceIoControl(payload.SafeFileHandle, setSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero),
                    $"Could not mark the fixture file sparse. Win32 error {Marshal.GetLastPInvokeError()}.");
            }
            payload.SetLength(bytes);
        }
        if (nestedBytes > 0)
        {
            var nested = Directory.CreateDirectory(Path.Combine(entry, "nested"));
            File.WriteAllBytes(Path.Combine(nested.FullName, "payload.bin"), new byte[nestedBytes]);
        }
        Directory.SetLastWriteTimeUtc(entry, writeTime.UtcDateTime);
        return entry;
    }

    private static void CreateDirectoryLink(string link, string target, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", link, target }
        };
        var result = CliChildProcessRunner.Run(start);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
    }

    private static async Task<string> RunStorageHealth(RetentionReclaimFixture fixture, string cache, params string[] options)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.ExecutionDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { typeof(CliArgumentParser).Assembly.Location, "storage-health" }
        };
        foreach (var arg in options) start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach (var name in new[]
        {
            "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA",
            "APPDATA", "ProgramFiles", "ProgramFiles(x86)", "DOTNET_ROOT", "DOTNET_ROOT(x86)"
        })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = fixture.ExecutionDirectory;
        start.Environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = Path.Combine(fixture.ExecutionDirectory, "registry");
        start.Environment[WorkerBuildScopeToolCacheIdentity.EnvironmentVariable] = cache;
        var result = await CliChildProcessRunner.RunAsync(start);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint controlCode, IntPtr input, int inputLength,
        IntPtr output, int outputLength, out uint bytesReturned, IntPtr overlapped);
}
