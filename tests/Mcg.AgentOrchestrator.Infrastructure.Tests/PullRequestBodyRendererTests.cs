using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: pure rendering with a private in-memory goal.
public sealed class PullRequestBodyRendererTests
{
    [Fact]
    public void Approved_fields_preserve_title_criteria_and_gate_but_redact_paths_and_path_users()
    {
        var goal = new AgentOrchestratorKernel().CreateGoal("""
            # Publish safely

            ## Acceptance criteria
            1. Run checks for C:\Users\Alice\repo\file.cs
            2. Verify Alice output and .orchestrator/private artifacts.
            """, [new TaskSpec(TaskId.New(), "Render pull request content", AgentRole.Developer)]);

        var rendered = PullRequestBodyRenderer.Render(goal, "gate-attempt-1", ["unit Alice", "check /home/Bob/private"],
            "unselected receipt: /home/Carol/receipt.log");

        Assert.Equal("Publish safely", rendered.Title);
        Assert.Contains("Publish safely", rendered.Body);
        Assert.Contains("1. Run checks", rendered.Body);
        Assert.Contains("2. Verify", rendered.Body);
        Assert.Contains("acceptance gate passed, attempt gate-attempt-1, checks: unit", rendered.Body);
        foreach (var forbidden in new[] { "Alice", "Bob", "Carol", "C:", "/home", ".orchestrator", "unselected receipt" })
            Assert.DoesNotContain(forbidden, rendered.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unsafe_attempt_identity_fails_before_a_body_can_be_published()
    {
        var goal = new AgentOrchestratorKernel().CreateGoal("Safe title",
            [new TaskSpec(TaskId.New(), "Render pull request content", AgentRole.Developer)]);
        Assert.Throws<InvalidOperationException>(() => PullRequestBodyRenderer.Render(goal, "C:\\Users\\Alice\\receipt", ["unit"]));
    }
}
