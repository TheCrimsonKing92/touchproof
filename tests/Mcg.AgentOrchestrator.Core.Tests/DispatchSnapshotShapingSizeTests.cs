using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class DispatchSnapshotShapingSizeTests
{
    [Fact]
    public void ManyDispatches_GoalSnapshot_IsSmallerThanEquivalentInlineShape()
    {
        const int dispatchCount = 20;
        var brief = new string('x', 4096);
        var goal = DispatchSnapshotShapingTests.CreateGoal(dispatchCount, brief);
        var shaped = goal.ToSnapshot();
        var inline = shaped with { Tasks = goal.Tasks.Select(task => task.ToSnapshot()).ToArray() };
        var oldTask = Assert.Single(inline.Tasks);
        Assert.Equal(dispatchCount, oldTask.DispatchHistory!.Count);
        Assert.All(oldTask.DispatchHistory, dispatch =>
        {
            Assert.Equal(brief, dispatch.BriefSnapshot);
            Assert.Null(dispatch.BriefSnapshotFromVersion);
            Assert.Equal(4, dispatch.ContextPackageReceipt!.Sections.Count);
            Assert.Null(dispatch.ContextPackageReceipt.SectionCount);
        });

        var options = new JsonSerializerOptions();
        var oldBytes = JsonSerializer.SerializeToUtf8Bytes(inline, options).Length;
        var newBytes = JsonSerializer.SerializeToUtf8Bytes(shaped, options).Length;

        Assert.True(newBytes < oldBytes, $"New shape {newBytes} bytes; old shape {oldBytes} bytes.");
        Assert.True(oldBytes - newBytes >= dispatchCount * Encoding.UTF8.GetByteCount(brief),
            "Savings must cover the history's repeated brief text.");
        Assert.NotNull(inline.Tasks[0].LastVerification);
        Assert.Equal(JsonSerializer.Serialize(inline.Tasks[0].LastVerification, options),
            JsonSerializer.Serialize(shaped.Tasks[0].LastVerification, options));
    }
}
