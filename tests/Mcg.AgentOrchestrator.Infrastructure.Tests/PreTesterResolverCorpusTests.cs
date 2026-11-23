using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using static Mcg.AgentOrchestrator.App.Orchestration.PreTesterReverseDependencyObservation;
using static PreTesterResolverObservationTests;

public sealed class PreTesterResolverCorpusTests
{
    [Fact]
    public void ReconstructsInventoryAndRunsBothIdentifiedArmsInRecordedOrder()
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        var path = MakeCorpus(fixture.Root);
        var seal = Receipt(path);
        File.Delete(Path.Combine(fixture.Root, PreTesterReverseDependencySelectionFixture.Project));
        var candidate = Build(fixture.Root, PreTesterResolverCorpus.CandidateRevision);
        var oracle = Build(fixture.Root, PreTesterResolverCorpus.OracleRevision);
        var receipt = Path.Combine(fixture.Root, "replay.json");
        var order = new List<(string, int)>();

        PreTesterResolverCorpus.Replay(path, seal.Sha256!, receipt, candidate, oracle, (build, row) =>
        {
            order.Add((build.Revision, row.Invocation.Ordinal));
            Assert.Equal(["A", "B", "A"], row.Inventory.Select(descriptor => descriptor.FullName));
            Assert.Equal(row.Invocation.Ordinal == 3 ? ["B", "A"] : new[] { "A", "B", "A" }, row.Invocation.Names);
            return new(row.Invocation.UnresolvedName, [], []);
        });

        Assert.Equal(new[]
        {
            (oracle.Revision, 1), (candidate.Revision, 1), (oracle.Revision, 2), (candidate.Revision, 2),
            (oracle.Revision, 3), (candidate.Revision, 3)
        }, order);
        var result = JsonSerializer.Deserialize<PreTesterResolverCorpus.ReplayReceipt>(File.ReadAllBytes(receipt))!;
        Assert.Equal(seal.Sha256, result.CorpusSha256);
        Assert.Equal(seal.WindowId, result.WindowId);
        var producer = Assert.Single(result.Producers);
        Assert.Equal(seal.SessionId, producer.SessionId);
        Assert.NotEqual(producer.AppSha256, result.Candidate.AppSha256);
        Assert.NotEqual(result.Candidate.AppSha256, result.Oracle.AppSha256);
        Assert.All(result.Comparisons, comparison => Assert.True(comparison.Equivalent));
        Assert.Equal(seal.Sha256, Sha256(path));
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("ordinal-with-new-digest")]
    [InlineData("candidate")]
    [InlineData("oracle-revision")]
    public void FailsBeforeEitherArmOnDigestOrdinalOrBuildMismatch(string mismatch)
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        var path = MakeCorpus(fixture.Root);
        var seal = Receipt(path);
        var candidate = Build(fixture.Root, PreTesterResolverCorpus.CandidateRevision);
        var oracle = Build(fixture.Root, PreTesterResolverCorpus.OracleRevision);
        var receipt = Path.Combine(fixture.Root, "replay.json");
        switch (mismatch)
        {
            case "inventory":
                File.WriteAllText(path, File.ReadAllText(path).Replace("\"Inventory\":[\"A\"", "\"Inventory\":[\"X\""));
                break;
            case "ordinal-with-new-digest":
                File.WriteAllText(path, File.ReadAllText(path).Replace("\"Ordinal\":1", "\"Ordinal\":2"));
                seal = seal with { Sha256 = Sha256(path) };
                File.WriteAllBytes(path + ".receipt.json", JsonSerializer.SerializeToUtf8Bytes(seal));
                break;
            case "candidate":
                File.AppendAllText(candidate.AppPath, "tampered");
                break;
            case "oracle-revision":
                File.WriteAllText(oracle.AppPath + ".git-head", candidate.Revision);
                break;
        }

        Assert.Throws<InvalidDataException>(() => PreTesterResolverCorpus.Replay(path, seal.Sha256!, receipt,
            candidate, oracle, (_, _) => throw new InvalidOperationException("A replay arm executed.")));
        Assert.False(File.Exists(receipt));
    }

    private static string MakeCorpus(string root)
    {
        var now = Start;
        var id = Guid.NewGuid();
        using var window = Window(root);
        using var tick = Assert.IsType<Tick>(window.BeginTick(id, () => now));
        Record(tick, tick.Enter()!.Value, ["A", "B", "A"]);
        Record(tick, tick.Enter()!.Value, ["A", "B", "A"]);
        Record(tick, tick.Enter()!.Value, ["B", "A"]);
        tick.Complete();
        now += Duration;
        Assert.Null(window.BeginTick(id, () => now));
        return CorpusPath(root, id);
    }

    private static PreTesterResolverCorpus.ReplayBuild Build(string root, string revision)
    {
        var app = Path.Combine(root, revision + ".dll");
        File.WriteAllText(app, "fixture App " + revision);
        File.WriteAllText(app + ".git-head", revision);
        return new(revision, revision, app, Sha256(app), []);
    }
}
