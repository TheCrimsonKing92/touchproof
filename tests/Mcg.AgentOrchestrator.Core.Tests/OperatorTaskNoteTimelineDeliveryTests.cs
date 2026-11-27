using Mcg.AgentOrchestrator.Core;
using Xunit;

public sealed class OperatorTaskNoteTimelineDeliveryTests
{
    private const string MiddleSegment = "preserve this operator instruction in the middle";

    [Theory]
    [InlineData(TaskComplexity.Complex)]
    [InlineData(TaskComplexity.Simple)]
    public void FormatTimelineEventKeepsCompleteOperatorTaskNote(TaskComplexity complexity)
    {
        var note = $" {CreateNote()} ";
        var evt = new ProgressEvent(GoalId.New(), TaskId.New(), ProgressKind.OperatorTaskNote, note, new FakeClock().UtcNow);

        var line = PromptContextFormatter.FormatTimelineEvent(evt, includeTimestamp: false, complexity);

        Assert.Equal($"- OperatorTaskNote: {note}", line);
        Assert.Contains(MiddleSegment, line, StringComparison.Ordinal);
        Assert.DoesNotContain("[truncated", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TaskComplexity.Complex, 170, 60, 970)]
    [InlineData(TaskComplexity.Simple, 100, 30, 1070)]
    public void FormatTimelineEventStillTrimsOverLimitMessagesOfOtherKinds(
        TaskComplexity complexity,
        int headLength,
        int tailLength,
        int omittedLength)
    {
        var message = CreateNote();
        var evt = new ProgressEvent(GoalId.New(), TaskId.New(), ProgressKind.TaskRetried, message, new FakeClock().UtcNow);

        var line = PromptContextFormatter.FormatTimelineEvent(evt, includeTimestamp: false, complexity);

        Assert.Equal($"- TaskRetried: {message[..headLength]} ...[truncated {omittedLength} chars]... {message[^tailLength..]}", line);
    }

    [Fact]
    public void TaskBriefRecentTimelineCarriesCompleteOperatorTaskNote()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(
            TaskId.New(),
            "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
            AgentRole.Developer);
        var goal = kernel.CreateGoal("Preserve the operator note in the task brief", [task]);
        var note = CreateNote();
        kernel.RecordOperatorTaskNote(goal.Id, task.Id, note);

        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

        var sections = brief.Split($"## Recent Timeline{Environment.NewLine}", StringSplitOptions.None);
        Assert.Equal(2, sections.Length);
        var line = Assert.Single(sections[1].Split(Environment.NewLine), text => text.Contains(" OperatorTaskNote: ", StringComparison.Ordinal));
        Assert.EndsWith($" OperatorTaskNote: {note}", line, StringComparison.Ordinal);
        Assert.Contains(MiddleSegment, line, StringComparison.Ordinal);
        Assert.DoesNotContain("[truncated", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerPromptTimelineCarriesCompleteOperatorTaskNote()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Update a tooltip label.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Preserve the operator note in the runner prompt", [task]);
        var agents = DefaultAgents();
        kernel.ActivateGoal(goal.Id, agents);
        var note = CreateNote();
        kernel.RecordOperatorTaskNote(goal.Id, task.Id, note);
        var provider = new FakeModelProvider("OpenAI", "Updated the tooltip label.");
        var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

        await runner.RunAsync(goal.Id, task.Id);

        Assert.NotNull(provider.LastRequest);
        var prompt = Assert.Single(provider.LastRequest.Messages).Content;
        var line = Assert.Single(prompt.Split(Environment.NewLine), text => text.StartsWith("- OperatorTaskNote: ", StringComparison.Ordinal));
        Assert.Equal($"- OperatorTaskNote: {note}", line);
        Assert.Contains(MiddleSegment, line, StringComparison.Ordinal);
    }

    private static string CreateNote()
    {
        const string head = "operator-note-head ";
        const string tail = " operator-note-tail";
        var firstHalf = $"{head}{new string('x', 550)} {MiddleSegment} ";
        return firstHalf + new string('y', 1200 - firstHalf.Length - tail.Length) + tail;
    }
}
