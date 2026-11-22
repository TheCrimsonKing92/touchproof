using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteExecutorOccupancyCleanupTests : IDisposable
{
    private const string Executor = "one";
    private const int LivePid = 1001;
    private const int ExitedPid = 1002;
    private static readonly DateTimeOffset OwnerStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "occupancy-cleanup-" + Guid.NewGuid().ToString("N"));
    private string Folder => RemoteExecutorOccupancy.Folder(_root, Executor);

    public RemoteExecutorOccupancyCleanupTests() => Directory.CreateDirectory(Folder);

    [Fact]
    public void IsOccupiedRemovesDeadAndReusedOwnerFilesAndKeepsLiveOnes()
    {
        WriteMixedFiles();

        Assert.True(RemoteExecutorOccupancy.IsOccupied(_root, Executor, StartTimeOf));
        AssertFiles("a-live.Gate.demand", "a-live.json");

        File.Delete(Path.Combine(Folder, "a-live.json"));
        WriteDemand("dead.Gate.demand", ExitedPid);

        Assert.False(RemoteExecutorOccupancy.IsOccupied(_root, Executor, StartTimeOf));
        AssertFiles("a-live.Gate.demand");
    }

    [Fact]
    public void LiveMarkersRemovesDeadAndReusedOwnerFilesAndReturnsOnlyLiveMarker()
    {
        WriteMixedFiles();

        var marker = Assert.Single(RemoteExecutorOccupancy.LiveMarkers(_root, Executor, StartTimeOf));

        Assert.Equal(("a-live", "Shadow"), marker);
        AssertFiles("a-live.Gate.demand", "a-live.json");
    }

    [Fact]
    public void UnparseableMarkerStaysAndHeldDeadMarkerDoesNotChangeOccupancy()
    {
        File.WriteAllText(Path.Combine(Folder, "malformed.json"), "{");
        File.WriteAllText(Path.Combine(Folder, "null.json"), "null");
        File.WriteAllText(Path.Combine(Folder, "malformed.Gate.demand"), "{");

        Assert.False(RemoteExecutorOccupancy.IsOccupied(_root, Executor, StartTimeOf));
        AssertFiles("malformed.Gate.demand", "malformed.json", "null.json");

        var heldPath = WriteMarker("a-held.json", ExitedPid, OwnerStart);
        var livePath = WriteMarker("z-live.json", LivePid, OwnerStart);
        var checkedHeldOwner = false;
        using (File.Open(heldPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(RemoteExecutorOccupancy.IsOccupied(_root, Executor, pid =>
            {
                checkedHeldOwner |= pid == ExitedPid;
                return StartTimeOf(pid);
            }));
            Assert.True(checkedHeldOwner);
            Assert.Equal("z-live", Assert.Single(RemoteExecutorOccupancy.LiveMarkers(_root, Executor, StartTimeOf)).Attempt);
            File.Delete(livePath);
            Assert.False(RemoteExecutorOccupancy.IsOccupied(_root, Executor, StartTimeOf));
            if (OperatingSystem.IsWindows()) Assert.True(File.Exists(heldPath));
        }

        Assert.False(RemoteExecutorOccupancy.IsOccupied(_root, Executor, StartTimeOf));
        AssertFiles("malformed.Gate.demand", "malformed.json", "null.json");
    }

    [Fact]
    public void IsOccupiedKeepsMarkerReplacedDuringLivenessCheck()
    {
        WriteMarker("attempt.json", ExitedPid, OwnerStart);

        RemoteExecutorOccupancy.IsOccupied(_root, Executor, pid =>
        {
            if (pid == ExitedPid) WriteMarker("attempt.json", LivePid, OwnerStart);
            return StartTimeOf(pid);
        });

        Assert.True(RemoteExecutorOccupancy.IsOccupied(_root, Executor, StartTimeOf));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupKeepsLiveReplacementAfterFinalRead(bool demand)
    {
        var name = demand ? "attempt.Gate.demand" : "attempt.json";
        if (demand) WriteDemand(name, ExitedPid);
        else WriteMarker(name, ExitedPid, OwnerStart);
        var path = Path.Combine(Folder, name);
        var original = File.ReadAllText(path);
        const string publicationName = "publication.tmp";
        if (demand) WriteDemand(publicationName, LivePid);
        else WriteMarker(publicationName, LivePid, OwnerStart);
        var publication = Path.Combine(Folder, publicationName);
        Task publish;
        bool brokeForPublication;

        using (var file = RemoteExecutorOccupancy.OpenForCleanup(path))
        {
            Assert.NotNull(file);
            Assert.Equal(original, file.ReadText());

            publish = Task.Run(() => File.Move(publication, path, overwrite: true));
            brokeForPublication = file.WaitForBreak(TimeSpan.FromSeconds(10));
            file.DeleteIfUncontended();
        }

        await publish.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(brokeForPublication);
        AssertFiles(name);
        if (demand)
            Assert.Equal("Gate", Assert.Single(RemoteExecutorSlotArbiter.LiveDemandClasses(
                _root, Executor, OwnerStart, StartTimeOf)));
        else
            Assert.True(RemoteExecutorOccupancy.IsOccupied(_root, Executor, StartTimeOf));
    }

    [Fact]
    public async Task OccupancyReadersSeeLiveReplacementWhileCleanupHasItOpen()
    {
        var path = WriteMarker("attempt.json", ExitedPid, OwnerStart);
        var original = File.ReadAllText(path);
        var publication = WriteMarker("publication.tmp", LivePid, OwnerStart);
        File.Move(publication, path, overwrite: true);

        using var file = RemoteExecutorOccupancy.OpenForCleanup(path);
        Assert.NotNull(file);
        Assert.NotEqual(original, file.ReadText());

        var occupancy = Task.Run(() => (RemoteExecutorOccupancy.IsOccupied(_root, Executor, StartTimeOf),
            RemoteExecutorOccupancy.LiveMarkers(_root, Executor, StartTimeOf)));
        var (occupied, markers) = await occupancy.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(occupied);
        Assert.Equal(("attempt", "Shadow"), Assert.Single(markers));
    }

    [Fact]
    public async Task PublicationSucceedsWhenReaderStillHoldsDeletedMarker()
    {
        var path = WriteMarker("attempt.json", ExitedPid, OwnerStart);
        var publication = WriteMarker("publication.tmp", LivePid, OwnerStart);

        using var file = RemoteExecutorOccupancy.OpenForCleanup(path);
        Assert.NotNull(file);
        using var priorReader = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        file.DeleteIfUncontended();

        var publish = Task.Run(() => File.Move(publication, path, overwrite: true));
        var brokeForPublication = file.WaitForBreak(TimeSpan.FromSeconds(10));
        file.Dispose();

        await publish.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(brokeForPublication);
        Assert.True(RemoteExecutorOccupancy.IsOccupied(_root, Executor, StartTimeOf));
    }

    private void WriteMixedFiles()
    {
        WriteMarker("a-live.json", LivePid, OwnerStart);
        WriteMarker("b-exited.json", ExitedPid, OwnerStart);
        WriteMarker("c-reused.json", LivePid, OwnerStart.AddDays(-15));
        WriteDemand("a-live.Gate.demand", LivePid);
        WriteDemand("dead.Gate.demand", ExitedPid);
    }

    private string WriteMarker(string name, int pid, DateTimeOffset start)
    {
        var path = Path.Combine(Folder, name);
        File.WriteAllText(path, JsonSerializer.Serialize(new { OwnerPid = pid, OwnerStartUtc = start, Class = "Shadow" }));
        return path;
    }

    private void WriteDemand(string name, int pid) =>
        File.WriteAllText(Path.Combine(Folder, name),
            JsonSerializer.Serialize(new RemoteExecutorSlotArbiter.Demand(pid, OwnerStart, "Gate", OwnerStart)));

    private static DateTimeOffset? StartTimeOf(int pid) => pid == LivePid ? OwnerStart : null;

    private void AssertFiles(params string[] expected) =>
        Assert.Equal(expected, Directory.EnumerateFiles(Folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
