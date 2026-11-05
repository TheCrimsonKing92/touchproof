using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

public sealed class LocalAdmissionOrderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "local-admission-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void KeyedShortCheck_IsFirstInLocalAdmissionOrder()
    {
        var checks = Enumerable.Range(0, 5).Select(index => Lane("Long" + index, 1000 + index * 25))
            .Append(Lane("Short", 37, ["host:unserved"])).ToArray();

        var order = LocalAdmissionOrder.Order(checks, []).Select(shard => shard.Index).ToArray();

        Assert.Equal([5, 4, 3, 2, 1, 0], order);
        Assert.Equal(5, RemoteLaneEarlyOffer.Order(checks).Last().Index);
    }

    [Fact]
    public void NoRemoteConfigured_OrderEqualsLongestFirst()
    {
        Check[] checks = [Lane("Keyed", 1, ["host:unserved"]), Lane("Build", 800, buildChange: true),
            Lane("Plain", 900), Lane("Tie", 800)];
        Seed(checks[0], 1000, 1100, 1200);
        using var scope = AcceptanceLaneDurationStore.PushRecordingScope(_root);
        Assert.Equal(1100d, AcceptanceLaneDurationStore.ResolveSortSeconds(checks[0]));
        Assert.All(checks, check => Assert.False(LocalAdmissionOrder.IsLocalBound(check, null)));

        var order = LocalAdmissionOrder.Order(checks, null).Select(shard => shard.Index).ToArray();

        Assert.Equal([0, 2, 1, 3], order);
        Assert.Equal(RemoteLaneEarlyOffer.Order(checks).Select(shard => shard.Index), order);
    }

    [Fact]
    public void LocalBoundGroup_UsesObservedMedianThenSeedAndIndex()
    {
        Check[] checks = [Lane("Observed", 2000, ["host:a"]), Lane("Seed", 700, ["host:b"]),
            Lane("Tie", 700, ["host:c"]), Lane("Pool", 3000)];
        Seed(checks[0], 20, 30, 1000);
        using var scope = AcceptanceLaneDurationStore.PushRecordingScope(_root);
        Assert.Equal(30d, AcceptanceLaneDurationStore.ResolveSortSeconds(checks[0]));
        Assert.Equal(700d, AcceptanceLaneDurationStore.ResolveSortSeconds(checks[1]));

        Assert.Equal([1, 2, 0, 3], LocalAdmissionOrder.Order(checks, []).Select(shard => shard.Index));
    }

    [Fact]
    public void RemoteOfferOrder_RemainsLongestFirstForEligibleChecks()
    {
        Check[] checks = [Lane("Short", 37, ["host:unserved"]), Lane("Pool", 1100),
            Lane("MachineLocal", 1000, ["machine"]), Lane("Serialized", 900, ["serialized"])];
        var configuration = new RemoteLaneExecutorConfiguration([new("fixture", 60)], checks.Select(check => check.Name).ToArray(), null)
        {
            MachineLocalResourceKeys = ["machine"], ExecutorSerializedResourceKeys = ["serialized"]
        };
        using var coordinator = Coordinator(configuration);
        var eligible = checks.Where(coordinator.IsEligible).ToArray();
        Assert.Equal(checks.Skip(1), eligible);
        Assert.Equal([1, 2, 3, 0], RemoteLaneEarlyOffer.Order(checks).Select(shard => shard.Index));
        Assert.Equal(eligible, RemoteLaneEarlyOffer.Order(eligible).Select(shard => shard.Check));
        Assert.Equal(eligible, LocalAdmissionOrder.Order(checks, coordinator.RemoteEligibleResourceKeys)
            .Select(shard => shard.Check).Where(check => eligible.Contains(check)));
    }

    [Fact]
    public void IsLocalBound_RecordsNoOutcome()
    {
        var check = Lane("Short", 37, ["host:unserved"]);
        var outcomes = new List<RemoteLaneOutcomeCode>();
        var configuration = new RemoteLaneExecutorConfiguration([new("fixture", 60)], [check.Name], null);
        using var coordinator = Coordinator(configuration, (_, code) => outcomes.Add(code));
        var ledger = RemoteExecutorHealthLedger.ResolveStorePath(_root);
        Assert.False(File.Exists(ledger));

        for (var index = 0; index < 3; index++)
            Assert.True(LocalAdmissionOrder.IsLocalBound(check, coordinator.RemoteEligibleResourceKeys));
        _ = LocalAdmissionOrder.Order([check], coordinator.RemoteEligibleResourceKeys).ToArray();

        Assert.Empty(outcomes);
        Assert.False(File.Exists(ledger));
        Assert.False(coordinator.IsEligible(check));
        Assert.Equal([RemoteLaneOutcomeCode.NotEligibleExclusiveResource], outcomes);
        Assert.True(File.Exists(ledger));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    public void IsLocalBound_UsesConfigurationKeysOrBuildChange(
        bool remoteConfigured, bool excludedKey, bool buildChange, bool expected)
    {
        var check = Lane("Check", 1, excludedKey ? ["unserved"] : [], buildChange);
        Assert.Equal(expected, LocalAdmissionOrder.IsLocalBound(check, remoteConfigured ? [] : null));
        Assert.False(LocalAdmissionOrder.IsLocalBound(Lane("Served", 1, ["machine", "serialized"]), ["machine", "serialized"]));
    }

    private RemoteLaneCoordinator Coordinator(RemoteLaneExecutorConfiguration configuration,
        Action<string, RemoteLaneOutcomeCode>? outcome = null) => new(configuration,
        new RemoteLaneCandidateIdentity("attempt", "goal", "commit", "tree", "main", "manifest"),
        _root, null, new FakeRemoteLaneExecutor(), TimeProvider.System, TimeSpan.FromSeconds(1), outcome);

    private void Seed(Check check, params double[] seconds)
    {
        using var scope = AcceptanceLaneDurationStore.PushRecordingScope(_root);
        foreach (var value in seconds)
            AcceptanceLaneDurationStore.Record(check, new AcceptanceCheckResult(check.Name, true, 0, null), TimeSpan.FromSeconds(value));
        AcceptanceLaneDurationStore.Flush();
    }

    private static Check Lane(string name, double seconds, string[]? keys = null, bool buildChange = false) => new()
    {
        Name = "infrastructure tests: " + name, Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", "FullyQualifiedName~" + name + "Tests"],
        EstimatedSerialSeconds = seconds, ExclusiveResourceKeys = keys ?? [], RequiresBuildSystemChange = buildChange
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
