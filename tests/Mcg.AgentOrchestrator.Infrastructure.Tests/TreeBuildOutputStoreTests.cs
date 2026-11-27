using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class TreeBuildOutputStoreTests : IDisposable
{
    private const string Tree = "wbc1:git-tree:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherTree = "wbc1:git-tree:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OldReceipt = """
        {"recorded_at":"2026-11-27T20:00:00+00:00","goal_id":null,"origin":"gate","tree_digest":"wbc1:git-tree:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","projects":["app.csproj"],"wall_ms":10,"cpu_ms":8,"cpu_ms_unavailable_reason":null,"cache":"none","version":1}
        """;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tree-build-output-{Guid.NewGuid():N}");
    private static readonly TreeBuildOutputIdentity Identity =
        new(["dotnet", "build", "app.csproj", "-p:A=1", "-p:A=2"], "Debug", "10.0.103", "fixture-os", "X64");

    private string StoreRoot => Path.Combine(_root, "store");
    private string Source => Path.Combine(_root, "source");
    private string Target => Path.Combine(_root, "target");

    [Fact]
    public void TryRestore_MatchingTreeAndIdentity_CopiesEveryByteAndKeepsOtherFiles()
    {
        WriteOutputs(Source);
        WriteFile(Target, "keep.txt", [7, 9]);
        WriteFile(Target, Path.Combine("bin", "app.dll"), [42]);
        var store = new TreeBuildOutputStore(StoreRoot);
        Assert.True(store.Publish(Tree, Identity, Source));

        var result = new TreeBuildOutputStore(StoreRoot).TryRestore(Tree, Identity, Target);

        Assert.True(result.Hit);
        Assert.Equal(2, result.FileCount);
        var files = Directory.GetFiles(Source, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(Source, file)).Order().ToArray();
        Assert.Equal(files.Append("keep.txt").Order(), Directory.GetFiles(Target, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(Target, file)).Order());
        Assert.All(files, file => Assert.Equal(File.ReadAllBytes(Path.Combine(Source, file)),
            File.ReadAllBytes(Path.Combine(Target, file))));
        Assert.Equal(new byte[] { 7, 9 }, File.ReadAllBytes(Path.Combine(Target, "keep.txt")));
    }

    [Theory]
    [InlineData("tree", "not-found")]
    [InlineData("configuration", "identity-differs")]
    [InlineData("sdk", "identity-differs")]
    [InlineData("arguments", "identity-differs")]
    [InlineData("argument-order", "identity-differs")]
    [InlineData("os", "identity-differs")]
    [InlineData("architecture", "identity-differs")]
    [InlineData("altered-file", "verification-failed")]
    [InlineData("extra-file", "verification-failed")]
    [InlineData("unresolved", "not-found")]
    [InlineData("partial-identity", "verification-failed")]
    public void TryRestore_UnmatchedOrInvalidSnapshot_RefusesWithoutTouchingTarget(string mismatch, string reason)
    {
        WriteOutputs(Source);
        var original = new byte[] { 32, 170 };
        var existing = Path.Combine("bin", "app.dll");
        WriteFile(Target, existing, original);
        var store = new TreeBuildOutputStore(StoreRoot);
        Assert.True(store.Publish(Tree, Identity, Source));
        var digest = mismatch switch { "tree" => OtherTree, "unresolved" => "unresolved", _ => Tree };
        var identity = mismatch switch
        {
            "configuration" => Identity with { Configuration = "Release" },
            "sdk" => Identity with { SdkVersion = "10.0.200" },
            "arguments" => Identity with { BuildArguments = [.. Identity.BuildArguments, "-p:B=1"] },
            "argument-order" => Identity with { BuildArguments = ["dotnet", "build", "app.csproj", "-p:A=2", "-p:A=1"] },
            "os" => Identity with { OperatingSystem = "another-os" },
            "architecture" => Identity with { Architecture = "Arm64" },
            "partial-identity" => Identity with { SdkVersion = "" },
            _ => Identity
        };
        if (mismatch == "altered-file")
        {
            var cached = Assert.Single(Directory.GetFiles(StoreRoot, "app.pdb", SearchOption.AllDirectories));
            File.WriteAllBytes(cached, [25]);
        }
        if (mismatch == "extra-file")
        {
            var cached = Assert.Single(Directory.GetFiles(StoreRoot, "app.pdb", SearchOption.AllDirectories));
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(cached)!, "extra.txt"), [25]);
        }

        var result = store.TryRestore(digest, identity, Target);

        Assert.False(result.Hit);
        Assert.Equal(reason, result.Refusal!.Value.ToReceiptReason());
        Assert.Equal(Path.Combine(Target, existing), Assert.Single(Directory.GetFiles(Target, "*", SearchOption.AllDirectories)));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Target, existing)));
    }

    [Fact]
    public void UnresolvedTree_SkipsStoreAccessOnPublishAndRestore()
    {
        WriteOutputs(Source);
        var store = new TreeBuildOutputStore(StoreRoot, contentHash: _ => throw new InvalidOperationException("Store accessed"));

        Assert.False(store.Publish("unresolved", Identity, Source));
        Assert.False(Directory.Exists(StoreRoot));
        Assert.Equal(TreeBuildOutputRefusal.NotFound, store.TryRestore("unresolved", Identity, Target).Refusal);
        Assert.False(Directory.Exists(StoreRoot));
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public void Publish_DifferentIdentitiesForSameTree_RetainsBothSnapshots()
    {
        WriteOutputs(Source);
        var store = new TreeBuildOutputStore(StoreRoot);
        var release = Identity with { Configuration = "Release" };
        Assert.True(store.Publish(Tree, Identity, Source));
        var file = Path.Combine("bin", "app.dll");
        WriteFile(Source, file, [23]);
        Assert.True(store.Publish(Tree, release, Source));

        Assert.True(store.TryRestore(Tree, Identity, Target).Hit);
        Assert.Equal(new byte[] { 0, 81, 255 }, File.ReadAllBytes(Path.Combine(Target, file)));
        Assert.True(store.TryRestore(Tree, release, Target).Hit);
        Assert.Equal(new byte[] { 23 }, File.ReadAllBytes(Path.Combine(Target, file)));
    }

    [Fact]
    public void Publish_ArtifactOwnershipAndWorkerDiagnostics_LeavesThemBehind()
    {
        WriteOutputs(Source);
        WriteFile(Source, DotnetBuildEnvironmentManager.ArtifactsOwnerFileName, [42]);
        WriteFile(Source, AcceptanceAttemptArtifactCustody.MarkerFileName, [42]);
        WriteFile(Source, Path.Combine("tmp", "compiler.scratch"), [42]);
        WriteFile(Source, Path.Combine("worker-host-builds", "compiler.log"), [42]);
        var store = new TreeBuildOutputStore(StoreRoot);
        Assert.True(store.Publish(Tree, Identity, Source));

        var result = store.TryRestore(Tree, Identity, Target);

        Assert.True(result.Hit);
        Assert.Equal(2, result.FileCount);
        Assert.Equal(new[] { "bin" }, Directory.GetFileSystemEntries(Target).Select(Path.GetFileName));
    }

    [Fact]
    public void TryRestore_ExpiredSnapshot_EvictsIt()
    {
        WriteOutputs(Source);
        var clock = new CacheClock();
        var store = new TreeBuildOutputStore(StoreRoot, clock);
        Assert.True(store.Publish(Tree, Identity, Source));
        Assert.Single(Directory.GetFiles(StoreRoot, "manifest.json", SearchOption.AllDirectories));
        clock.Now = clock.Now.AddDays(8);

        Assert.Equal(TreeBuildOutputRefusal.NotFound, store.TryRestore(Tree, Identity, Target).Refusal);
        Assert.Empty(Directory.GetFiles(StoreRoot, "manifest.json", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public async Task EnsureAsync_SameTreeAcrossBuildOrigins_SkipsManagedBuildAndRecordsHit()
    {
        var store = new TreeBuildOutputStore(StoreRoot);
        var reuse = new TreeBuildOutputReuse(store, (_, _) => Task.FromResult<TreeBuildOutputIdentity?>(Identity));
        var invocations = new List<string>();
        var receipts = new List<string>();
        var ledger = new BuildReceiptLedger((_, line) => receipts.Add(line));

        var first = await Run(Tree, "pre-review-evidence", Source);
        Assert.True(first.Run.Result.Passed);
        Assert.Equal(new[] { Source }, invocations);
        var second = await Run(Tree, "gate", Target);
        Assert.True(second.Run.Result.Passed);
        Assert.True(second.ContributesToCheck);
        Assert.StartsWith("tree-output-reuse status=hit", second.Run.Result.ResultSummary);
        Assert.Equal(new[] { Source }, invocations);
        var output = Path.Combine("bin", "app.dll");
        Assert.Equal(File.ReadAllBytes(Path.Combine(Source, output)), File.ReadAllBytes(Path.Combine(Target, output)));
        var thirdPath = Path.Combine(_root, "third");
        var third = await Run(OtherTree, "gate", thirdPath);
        Assert.True(third.Run.Result.Passed);
        Assert.Equal(new[] { Source, thirdPath }, invocations);

        Assert.Collection(receipts.Select(Parse),
            receipt =>
            {
                Assert.Equal("pre-review-evidence", receipt.Origin);
                Assert.Equal(Tree, receipt.TreeDigest);
                Assert.Equal("miss", receipt.Cache);
                Assert.Equal("not-found", receipt.RefusalReason);
            },
            receipt =>
            {
                Assert.Equal("gate", receipt.Origin);
                Assert.Equal(Tree, receipt.TreeDigest);
                Assert.Equal("hit", receipt.Cache);
                Assert.Null(receipt.RefusalReason);
                Assert.Equal(0d, receipt.CpuMs);
            },
            receipt =>
            {
                Assert.Equal(OtherTree, receipt.TreeDigest);
                Assert.Equal("miss", receipt.Cache);
                Assert.Equal("not-found", receipt.RefusalReason);
            });

        Task<DotnetTestBuildPhaseResult> Run(string digest, string origin, string artifacts)
        {
            var phase = new DotnetTestBuildPhase(Identity.BuildArguments, null);
            var environment = new DotnetBuildEnvironment("lease", _root, artifacts,
                Path.Combine(_root, "lock"), ["--artifacts-path", artifacts], "owner");
            var hooks = new AcceptanceDotnetBuildPhaseHooks(
                (_, _, _, owned) =>
                {
                    Assert.Same(environment, owned);
                    invocations.Add(artifacts);
                    WriteOutputs(artifacts);
                    return Task.FromResult((new AcceptanceCheckResult("build", true, 0, null, artifacts), false));
                },
                _ => environment, value => value, () => throw new InvalidOperationException("No base cache expected"),
                (_, _) => throw new InvalidOperationException("No base cache identity expected"),
                ReceiptSite: new BuildReceiptSite(_root, "goal", origin, digest, ledger, reuse));
            return AcceptanceDotnetBuildPhase.EnsureAsync(phase, origin, hooks);
        }
    }

    [Fact]
    public void ReceiptAndReport_OldLinesParseAndHitsAreExcludedFromRepeatedTrees()
    {
        var old = Parse(OldReceipt);
        Assert.Null(old.RefusalReason);
        var miss = old with { Cache = "miss", RefusalReason = "identity-differs" };
        var serialized = miss.ToJson();
        Assert.Contains("\"refusal_reason\":\"identity-differs\"", serialized, StringComparison.Ordinal);
        Assert.Equal("identity-differs", Parse(serialized).RefusalReason);
        var hit = old with { Origin = "pre-review-evidence", Cache = "hit", CpuMs = 0 };

        var report = new BuildReceiptReport([serialized, hit.ToJson()]);

        Assert.Equal(0, report.Skipped);
        Assert.Equal(1, Assert.Single(report.Origins, origin => origin.Origin == "pre-review-evidence").CacheHits);
        Assert.Empty(report.Repeats);
    }

    private static BuildReceipt Parse(string line)
    {
        Assert.True(BuildReceipt.TryParse(line, out var receipt));
        return Assert.IsType<BuildReceipt>(receipt);
    }

    private static void WriteOutputs(string directory)
    {
        WriteFile(directory, Path.Combine("bin", "app.dll"), [0, 81, 255]);
        WriteFile(directory, Path.Combine("bin", "symbols", "app.pdb"), [70, 71]);
    }

    private static void WriteFile(string directory, string relativePath, byte[] content)
    {
        var path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class CacheClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
