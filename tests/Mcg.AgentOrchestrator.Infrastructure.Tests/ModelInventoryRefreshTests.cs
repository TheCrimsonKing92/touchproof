using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe. Each case uses its own files, fixture responses and clock.
public sealed class ModelInventoryRefreshTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshPersistsFixtureListingsAndSourceStatus(bool unauthenticated)
    {
        var root = CreateTempDirectory();
        var cachePath = Path.Combine(root, "models_cache.json");
        var inventoryPath = Path.Combine(root, "model-inventory.json");
        await File.WriteAllTextAsync(cachePath, """
            {"fetched_at":"2026-11-19T10:00:00Z","client_version":"1","models":[{"slug":"Gpt-Fixture"}]}
            """, TestContext.Current.CancellationToken);
        var grok = unauthenticated
            ? new WorkerProcessRunResult(1, "", "You are not authenticated")
            : new WorkerProcessRunResult(0, "Available models:\n  grok-fixture\n", "");
        const string anthropic = """{"data":[{"id":"claude-fixture"}],"has_more":false}""";
        var profiles = WorkerProfileCatalog.Default();
        var clock = new FixtureClock();

        await ModelInventoryRefresher.RefreshAsync(inventoryPath, profiles, [],
            (profile, source, token) => source switch
            {
                ModelDiscoverySource.CodexCache => ModelInventorySources.ReadCodexAsync(cachePath, token),
                ModelDiscoverySource.GrokModels => Task.FromResult(ModelInventorySources.ParseGrok(grok)),
                ModelDiscoverySource.AnthropicModels => Task.FromResult(ModelInventorySources.ParseAnthropic(anthropic, out _)),
                _ => throw new ArgumentOutOfRangeException(nameof(source))
            },
            (_, _, _) => throw new InvalidOperationException("Listing entries must never be probed"),
            clock, TestContext.Current.CancellationToken);

        var saved = ModelInventoryStore.Load(inventoryPath);
        Assert.Equal(saved.Entries.OrderBy(entry => entry.Profile, StringComparer.OrdinalIgnoreCase), saved.Entries);
        Assert.Equal(unauthenticated ? 3 : 4, saved.Entries.Count);
        Assert.All(saved.Entries, entry =>
        {
            Assert.Equal(clock.GetUtcNow(), entry.DiscoveredAt);
            Assert.Equal(TimeSpan.Zero, entry.DiscoveredAt.Offset);
            Assert.Equal(ModelProbeStatus.NotProbed, entry.ProbeStatus);
            Assert.True(entry.Eligible);
        });
        Assert.Equal(2, saved.Find("openai", "gpt-fixture").Count());
        AssertEntry("OpenAI", "Gpt-Fixture", "codex-cli", ModelDiscoverySource.CodexCache);
        AssertEntry("OpenAI", "Gpt-Fixture", WorkerProfileDispatcher.OpenAiLunaSubscriptionProfileName, ModelDiscoverySource.CodexCache);
        AssertEntry("Anthropic", "claude-fixture", "claude-cli", ModelDiscoverySource.AnthropicModels);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:00:00Z"), saved.Entries.Single(entry => entry.Profile == "codex-cli").SourceTimestamp);
        var grokStatus = Assert.Single(saved.Sources, source => source.Source == ModelDiscoverySource.GrokModels);
        Assert.Equal("xAI", grokStatus.Provider);
        Assert.Equal("grok-cli", grokStatus.Profile);
        Assert.Equal(unauthenticated ? ModelSourceStatus.Unauthenticated : ModelSourceStatus.Available, grokStatus.Status);
        if (unauthenticated)
            Assert.Empty(saved.Entries.Where(entry => entry.Provider == "xAI"));
        else
            AssertEntry("xAI", "grok-fixture", "grok-cli", ModelDiscoverySource.GrokModels);

        void AssertEntry(string provider, string model, string profile, ModelDiscoverySource source)
        {
            var entry = Assert.Single(saved.Entries, entry => entry.Provider == provider &&
                entry.Model == model && entry.Profile == profile);
            Assert.Equal(source, entry.Source);
        }
    }

    [Fact]
    public async Task AFailedReprobeReplacesSuccessfulEvidenceAndKeepsDeclaration()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "model-inventory.json");
        var declarationPath = Path.Combine(root, "model-declarations.json");
        var declaration = new ModelDeclaration("Anthropic", "claude-haiku-5-5", "claude-cli");
        ModelInventoryStore.UpdateDeclarations(declarationPath, _ => [declaration]);
        var profiles = new WorkerProfileCatalog([WorkerProfileCatalog.Default().GetRequired("claude-cli")]);
        var token = TestContext.Current.CancellationToken;

        await RefreshAsync(new(ModelProbeStatus.Succeeded));
        Assert.True(Assert.Single(ModelInventoryStore.Load(path).Entries).Eligible);
        await RefreshAsync(new(ModelProbeStatus.Failed, "Timed out"));

        var entry = Assert.Single(ModelInventoryStore.Load(path).Entries);
        Assert.False(entry.Eligible);
        Assert.Equal(ModelProbeStatus.Failed, entry.ProbeStatus);
        Assert.Equal("Timed out", entry.ProbeDetail);
        Assert.Equal(declaration, Assert.Single(ModelInventoryStore.LoadDeclarations(declarationPath)));

        Task<ModelInventory> RefreshAsync(ModelProbeResult result) =>
            ModelInventoryRefresher.RefreshAsync(path, profiles, ModelInventoryStore.LoadDeclarations(declarationPath),
                (_, _, _) => Task.FromResult(new ModelListing(ModelSourceStatus.NotConfigured, [])),
                (_, _, _) => Task.FromResult(result), new FixtureClock(), token);
    }

    private sealed class FixtureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    }
}
