using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure packing and a uniquely owned temporary history store.
public sealed class AcceptanceLaneWallModelPackerTests
{
    [Fact]
    public void FixtureHistory_LongSerialClassesSeparate_AndCollectionStaysWhole()
    {
        var root = Path.Combine(Path.GetTempPath(), "wall-packing-" + Guid.NewGuid().ToString("N"));
        try
        {
            var history = AcceptanceClassDurationHistoryStore.ResolveStorePath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(history)!);
            File.Copy(Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "tests",
                "Mcg.AgentOrchestrator.Infrastructure.Tests", "TestData", "Fixtures", "LaneWallReplay", "history.json"), history);
            var inputs = AcceptanceClassDurationHistoryStore.ReadModelInputs(root, 5, out var error);
            Assert.Null(error);
            Assert.Equal(6, inputs.Count);
            AcceptanceTestClassDescriptor[] classes = [new("Fixture.LargeA", null), new("Fixture.LargeB", null),
                new("Fixture.LongA", null), new("Fixture.LongB", null),
                new("Fixture.CollectionA", "run-alone"), new("Fixture.CollectionB", "run-alone")];
            var layout = AcceptanceBalancedLanePacker.Pack(classes,
                inputs.ToDictionary(pair => pair.Key, pair => pair.Value.Seconds), new() { LaneCount = 3 }, inputs);
            Assert.True(inputs["Fixture.LongA"].Seconds < inputs["Fixture.LargeA"].Seconds);
            Assert.True(inputs["Fixture.LongB"].Seconds < inputs["Fixture.LargeB"].Seconds);
            Assert.NotEqual(Owner("Fixture.LongA"), Owner("Fixture.LongB"));
            Assert.Equal(Owner("Fixture.CollectionA"), Owner("Fixture.CollectionB"));
            Assert.Equal(120, layout.Lanes.Max(lane => lane.PredictedWallSeconds));
            // Negative control: the historical sum-only policy stacks both 90s classes.
            var old = AcceptanceBalancedLanePacker.Pack(classes,
                inputs.ToDictionary(pair => pair.Key, pair => pair.Value.Seconds), new() { LaneCount = 3 });
            Assert.Contains(old.Lanes, lane => lane.Members.Contains("Fixture.LongA") && lane.Members.Contains("Fixture.LongB"));
            string Owner(string name) => Assert.Single(layout.Lanes.Where(lane => lane.Members.Contains(name))).Name;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Predict_SerialCollectionsAndSlotWork_RespectIndependentSchedulingBounds()
    {
        var model = new AcceptanceLaneWallModel(2);
        Assert.Equal(50, model.Predict([new(20, 20, true), new(10, 10, true), new(20, 20, false), new(20, 20, false)]));
        Assert.Equal(30, model.Predict([new(20, 20, false), new(20, 20, false), new(20, 20, false)]));
        Assert.Equal(100, model.Predict([new(100, 0, false), new(10, 10, false)]));
        Assert.Equal(30, new AcceptanceLaneWallModel(1).Predict([new(10, null, false), new(20, null, false)]));
        Assert.Throws<ArgumentException>(() => model.Predict([new(1, double.NaN, false)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AcceptanceLaneWallModel(0).Predict([]));
    }
}
