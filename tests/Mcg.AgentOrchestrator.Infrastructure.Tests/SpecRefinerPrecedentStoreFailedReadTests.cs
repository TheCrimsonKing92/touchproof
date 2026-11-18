using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: each test owns a unique directory and only opens its own files.
public sealed class SpecRefinerPrecedentStoreFailedReadTests : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "precedent-failed-read-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly SpecRefinerPrecedentStore _store;

    public SpecRefinerPrecedentStoreFailedReadTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "spec-refiner-precedents.json");
        _store = new SpecRefinerPrecedentStore(_path);
    }

    [Theory]
    [InlineData("[{")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("{}")]
    public async Task Record_InvalidSnapshot_ThrowsWithPathAndPreservesBytes(string content)
    {
        var seed = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(_path, seed);

        var error = await Assert.ThrowsAsync<SpecRefinerPrecedentStoreReadException>(
            () => _store.RecordPrecedentAsync("new-topic", "choice", "reason"));

        Assert.Equal(_path, error.FilePath);
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
        Assert.Equal(seed, await File.ReadAllBytesAsync(_path));
        Assert.False(File.Exists(_path + ".tmp"));
        Assert.False(File.Exists(_path + ".bak"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_CanceledToken_PropagatesAndPreservesBytes(bool lookup)
    {
        var seed = await SeedAsync(OriginalEntries());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (lookup)
                await _store.TryGetPrecedentAsync("first", cancellation.Token);
            else
                await _store.RecordPrecedentAsync("new-topic", "choice", "reason", cancellation.Token);
        });

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(seed, await File.ReadAllBytesAsync(_path));
        Assert.False(File.Exists(_path + ".tmp"));
        Assert.False(File.Exists(_path + ".bak"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    [InlineData("[]")]
    public async Task Record_EmptyStore_PublishesSingleEntry(string? content)
    {
        if (content is not null)
            await File.WriteAllTextAsync(_path, content);
        else
            Directory.Delete(_root);

        await _store.RecordPrecedentAsync("new-topic", "choice", "reason");

        var entry = Assert.Single(ReadEntries(await File.ReadAllBytesAsync(_path)));
        Assert.Equal("new-topic", entry.ForkKind);
        Assert.Equal("choice", entry.Choice);
        Assert.Equal("reason", entry.Rationale);
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public async Task Record_ExclusiveHandle_AbortsAndRetriesWithoutDuplicates()
    {
        var originals = OriginalEntries();
        var seed = await SeedAsync(originals);
        using (var held = File.Open(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // This handle blocks the initial read; the contract requires only a throw.
            await Assert.ThrowsAnyAsync<Exception>(
                () => _store.RecordPrecedentAsync("new-topic", "choice", "reason"));

            held.Position = 0;
            using var snapshot = new MemoryStream();
            await held.CopyToAsync(snapshot);
            Assert.Equal(seed, snapshot.ToArray());
            Assert.Equal(originals, ReadEntries(snapshot.ToArray()));
        }

        await _store.RecordPrecedentAsync("new-topic", "choice", "reason");
        await _store.RecordPrecedentAsync("NEW-TOPIC", "choice", "reason");

        var entries = ReadEntries(await File.ReadAllBytesAsync(_path));
        Assert.Equal(originals.Length + 1, entries.Length);
        Assert.Single(entries, p => string.Equals(p.ForkKind, "new-topic", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(originals, entries.Where(p => p.ForkKind != "NEW-TOPIC").ToArray());
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public async Task Record_NewTopic_PreservesAllOriginalFieldsAndBackup()
    {
        var originals = OriginalEntries();
        var seed = await SeedAsync(originals);

        await _store.RecordPrecedentAsync("new-topic", "choice", "reason");

        var entries = ReadEntries(await File.ReadAllBytesAsync(_path));
        Assert.Equal(4, entries.Length);
        foreach (var original in originals)
            Assert.Equal(original, Assert.Single(entries, p => p.ForkKind == original.ForkKind));
        var added = Assert.Single(entries, p => p.ForkKind == "new-topic");
        Assert.Equal("choice", added.Choice);
        Assert.Equal("reason", added.Rationale);
        Assert.Equal(seed, await File.ReadAllBytesAsync(_path + ".bak"));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public async Task Record_PublishDenied_PreservesLiveBytesAndCleansTemp()
    {
        var seed = await SeedAsync(OriginalEntries());
        // Reading is allowed, but replacement requires delete sharing.
        using (File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.NotNull(await _store.TryGetPrecedentAsync("first"));
            await Assert.ThrowsAnyAsync<IOException>(
                () => _store.RecordPrecedentAsync("new-topic", "choice", "reason"));

            Assert.Equal(seed, await File.ReadAllBytesAsync(_path));
            Assert.False(File.Exists(_path + ".tmp"));
        }

        await _store.RecordPrecedentAsync("new-topic", "choice", "reason");
        Assert.Single(ReadEntries(await File.ReadAllBytesAsync(_path)), p => p.ForkKind == "new-topic");
        Assert.Equal(seed, await File.ReadAllBytesAsync(_path + ".bak"));
    }

    [Fact]
    public async Task Lookup_InvalidSnapshot_ReturnsNullWithoutRepair()
    {
        var seed = Encoding.UTF8.GetBytes("[{");
        await File.WriteAllBytesAsync(_path, seed);
        var backup = JsonSerializer.SerializeToUtf8Bytes(OriginalEntries(), SerializerOptions);
        await File.WriteAllBytesAsync(_path + ".bak", backup);

        Assert.Null(await _store.TryGetPrecedentAsync("first"));

        Assert.Equal(seed, await File.ReadAllBytesAsync(_path));
        Assert.Equal(backup, await File.ReadAllBytesAsync(_path + ".bak"));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    private async Task<byte[]> SeedAsync(SpecRefinerPrecedent[] entries)
    {
        var seed = JsonSerializer.SerializeToUtf8Bytes(entries, SerializerOptions);
        await File.WriteAllBytesAsync(_path, seed);
        return seed;
    }

    private static SpecRefinerPrecedent[] ReadEntries(byte[] bytes) =>
        JsonSerializer.Deserialize<SpecRefinerPrecedent[]>(bytes, SerializerOptions)
        ?? throw new InvalidOperationException("Expected a precedent array.");

    private static SpecRefinerPrecedent[] OriginalEntries() =>
    [
        new("first", "one", "first reason", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "item-1", "goal-1", "answer-1", 1),
        new("second", "two", "second reason", new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero),
            "item-2", "goal-2", "answer-2", 2),
        new("third", "three", "third reason", new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero))
    ];

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
