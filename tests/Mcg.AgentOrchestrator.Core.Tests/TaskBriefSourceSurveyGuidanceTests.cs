using Mcg.AgentOrchestrator.Core;

// Parallel-safe: in-memory kernels and one uniquely owned temporary context directory.
public sealed class TaskBriefSourceSurveyGuidanceTests
{
    [Theory]
    [InlineData(AgentRole.Developer, "Update the label.", TaskComplexity.Simple)]
    [InlineData(AgentRole.Developer,
        "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
        TaskComplexity.Complex)]
    [InlineData(AgentRole.Researcher, "Inspect the current label.", TaskComplexity.Simple)]
    public void TaskBrief_WorkerRoles_UsesFileSourceSurvey(
        AgentRole role, string description, TaskComplexity complexity)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), description, role);
        var goal = kernel.CreateGoal("Maintain the label.", [task]);
        Assert.Equal(complexity, TaskComplexityEstimator.Estimate(description, goal.Objective, role));

        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

        Assert.Contains("source-survey.md in the context directory", brief, StringComparison.Ordinal);
        Assert.Contains("do not attempt to reach orchestrator state.", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/source-survey", brief, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dashboard", brief, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AgentRole.Developer, TaskComplexity.Simple, true)]
    [InlineData(AgentRole.Developer, TaskComplexity.Complex, true)]
    [InlineData(AgentRole.Planner, TaskComplexity.Simple, true)]
    [InlineData(AgentRole.Planner, TaskComplexity.Complex, true)]
    [InlineData(AgentRole.Tester, TaskComplexity.Simple, false)]
    [InlineData(AgentRole.Tester, TaskComplexity.Complex, false)]
    [InlineData(AgentRole.Reviewer, TaskComplexity.Simple, false)]
    [InlineData(AgentRole.Reviewer, TaskComplexity.Complex, false)]
    public void TaskBrief_ReadProcedure_UsesRoleAcrossComplexities(
        AgentRole role, TaskComplexity complexity, bool includesProcedure)
    {
        var description = complexity == TaskComplexity.Simple
            ? "Update the label."
            : "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.";
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), description, role);
        var goal = kernel.CreateGoal("Maintain the label.", [task]);
        Assert.Equal(complexity, TaskComplexityEstimator.Estimate(description, goal.Objective, role));

        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;
        var lines = brief.Split('\n');
        string[][] steps =
        [
            ["this session already printed the lines you need", "including in supplied excerpts", "use those lines"],
            ["their location is unknown", "with one `rg -n` or Grep search", "Read the needed range once", "40 lines before and 40 lines after", "at file boundaries"],
            ["fewer than 300 lines or you need most of it", "only once per session", "Batch known ranges into one command"],
            ["Reread previously printed lines only after you changed the file", "a build or test reported a line in it", "additional unseen lines in a bounded range"]
        ];

        var previousStep = -1;
        foreach (var fragments in steps)
        {
            if (!includesProcedure)
            {
                Assert.All(fragments, fragment => Assert.DoesNotContain(fragment, brief, StringComparison.Ordinal));
                continue;
            }

            var step = Array.FindIndex(lines, line => line.Contains(fragments[0], StringComparison.Ordinal));
            Assert.True(step > previousStep, $"Read procedure step missing or out of order: {fragments[0]}");
            Assert.All(fragments, fragment => Assert.Contains(fragment, lines[step], StringComparison.Ordinal));
            previousStep = step;
        }
    }

    [Fact]
    public void TaskBrief_PlannerWithDurableResearch_OmitsSourceDiscovery()
    {
        var context = Directory.CreateTempSubdirectory("source-survey-guidance-");
        try
        {
            File.WriteAllText(Path.Combine(context.FullName, "research-notes.md"), "The label is defined in source.");
            var kernel = new AgentOrchestratorKernel();
            var researcher = new TaskSpec(TaskId.New(), "Inspect the current label.", AgentRole.Researcher);
            var planner = new TaskSpec(TaskId.New(), "Plan the label update.", AgentRole.Planner);
            var goal = kernel.CreateGoal("Maintain the label.", [researcher, planner]);
            Assert.False(ScoutRoundPolicy.IsScoutPlanner(goal, planner));

            var brief = kernel.BuildTaskBrief(goal.Id, planner.Id,
                workingDirectory: context.FullName, contextDirectory: context.FullName).Content;

            Assert.Contains("Use the complete Durable Research Notes supplied in the context package", brief, StringComparison.Ordinal);
            Assert.DoesNotContain("source-survey.md in the context directory", brief, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/api/source-survey", brief, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            context.Delete(recursive: true);
        }
    }
}
