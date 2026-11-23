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

        var corpus = PreTesterResolverCorpus.Read(path, seal.Sha256!);
        Assert.Equal(["A", "B", "A"], corpus.Cases[0].Inventory.Select(descriptor => descriptor.FullName));
        Assert.Equal(["A", "B", "A"], corpus.Cases[1].Invocation.Names);
        var candidatePath = WriteBuild(candidate, fixture.Root, "candidate");
        var oraclePath = WriteBuild(oracle, fixture.Root, "oracle");
        using var error = new StringWriter();
        Assert.Equal(0, PreTesterResolverReplayCommand.Run(
            [PreTesterResolverReplayCommand.Name, path, seal.Sha256!, receipt, candidatePath, oraclePath], error));
        Assert.Equal("", error.ToString());

        var result = JsonSerializer.Deserialize<PreTesterResolverCorpus.ReplayReceipt>(File.ReadAllBytes(receipt))!;
        Assert.Equal(seal.Sha256, result.CorpusSha256);
        Assert.Equal(seal.WindowId, result.WindowId);
        var producer = Assert.Single(result.Producers);
        Assert.Equal(seal.SessionId, producer.SessionId);
        Assert.NotEqual(producer.AppSha256, result.Candidate.AppSha256);
        Assert.NotEqual(result.Candidate.AppSha256, result.Oracle.AppSha256);
        Assert.Equal([1, 2, 3], result.Comparisons.Select(comparison => comparison.Ordinal));
        Assert.All(result.Comparisons, comparison => Assert.True(comparison.Equivalent));
        Assert.All(result.Comparisons.Take(2), comparison =>
            Assert.Contains("class=A kind=reverse-dependency-unresolved", Assert.Single(comparison.Candidate.Diagnostics)));
        Assert.Equal("PRE_TESTER_GUARD_SELECTED class=none kind=reverse-dependency-skipped-cost fixture-cost",
            Assert.Single(result.Comparisons[2].Oracle.Diagnostics));
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
            candidate, oracle));
        Assert.False(File.Exists(receipt));
    }

    [Fact]
    public void AVerifiedDigestForANonExecutableCannotProduceADecisionReceipt()
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        var path = MakeCorpus(fixture.Root);
        var candidate = Build(fixture.Root, PreTesterResolverCorpus.CandidateRevision);
        var oracle = Build(fixture.Root, PreTesterResolverCorpus.OracleRevision);
        File.WriteAllText(candidate.AppPath, "this is not an executable");

        var receipt = Path.Combine(fixture.Root, "replay.json");
        Assert.Throws<BadImageFormatException>(() => PreTesterResolverCorpus.Replay(path, Receipt(path).Sha256!,
            receipt, candidate with { AppSha256 = Sha256(candidate.AppPath) }, oracle));
        Assert.False(File.Exists(receipt));
    }

    private static string MakeCorpus(string root)
    {
        var now = Start;
        var id = Guid.NewGuid();
        using var window = Window(root);
        using var tick = Assert.IsType<Tick>(window.BeginTick(id, () => now));
        var project = PreTesterReverseDependencySelectionFixture.Project;
        for (var index = 0; index < 2; index++)
            tick.Record(tick.Enter()!.Value, BeforeTester, project, ["A", "B", "A"],
                [new("A", null), new("B", null), new("A", null)], ["src/Shipping.cs"], "A", null);
        tick.Record(tick.Enter()!.Value, BeforeTester, project, ["B", "A"],
            [new("A", null), new("B", null)], ["src/Shipping.cs"], null, new(false, "fixture-cost"));
        tick.Complete();
        now += Duration;
        Assert.Null(window.BeginTick(id, () => now));
        return CorpusPath(root, id);
    }

    private static PreTesterResolverCorpus.ReplayBuild Build(string root, string revision)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, revision));
        var source = typeof(PreTesterResolverCorpus).Assembly.Location;
        var app = Path.Combine(directory.FullName, Path.GetFileName(source));
        foreach (var dependency in Directory.EnumerateFiles(Path.GetDirectoryName(source)!, "Mcg.AgentOrchestrator.*.dll"))
            File.Copy(dependency, Path.Combine(directory.FullName, Path.GetFileName(dependency)));
        // Distinct valid PE streams for the local binding test. The production command checks the same
        // marker and executable digest for the actual candidate and original-loop build.
        File.AppendAllText(app, revision);
        var stagedRevision = Sha256(app)[..40];
        File.WriteAllText(app + ".git-head", stagedRevision);
        return new(revision, stagedRevision, app, Sha256(app), ["fixture build with distinct PE overlay"]);
    }

    private static string WriteBuild(PreTesterResolverCorpus.ReplayBuild build, string root, string arm)
    {
        var path = Path.Combine(root, arm + "-build.json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(build));
        return path;
    }
}
