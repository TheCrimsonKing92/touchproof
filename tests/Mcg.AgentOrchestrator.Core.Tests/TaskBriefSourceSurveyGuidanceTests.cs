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
    [InlineData(AgentRole.Developer, TaskComplexity.Simple, true, 9500)]
    [InlineData(AgentRole.Developer, TaskComplexity.Complex, true, 9500)]
    [InlineData(AgentRole.Planner, TaskComplexity.Simple, true, 7000)]
    [InlineData(AgentRole.Planner, TaskComplexity.Complex, true, 7000)]
    [InlineData(AgentRole.Tester, TaskComplexity.Simple, false, 8500)]
    [InlineData(AgentRole.Tester, TaskComplexity.Complex, false, 8500)]
    [InlineData(AgentRole.Reviewer, TaskComplexity.Simple, false, 8000)]
    [InlineData(AgentRole.Reviewer, TaskComplexity.Complex, false, 8000)]
    public void TaskBrief_ReadProcedure_UsesRoleAcrossComplexities(
        AgentRole role, TaskComplexity complexity, bool includesProcedure, int contextBudget)
    {
        var description = complexity == TaskComplexity.Simple
            ? "Update the label."
            : "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.";
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), description, role);
        var goal = kernel.CreateGoal("Maintain the label.", [task]);
        Assert.Equal(complexity, TaskComplexityEstimator.Estimate(description, goal.Objective, role));

        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;
        var lines = brief.ReplaceLineEndings("\n").Split('\n');
        string[][] steps =
        [
            ["Before each file read", "reuse needed lines already printed this session", "including supplied excerpts"],
            ["location is unknown", "search once with `rg -n` or Grep", "Read the needed unread range once", "40 lines on each side", "within file bounds"],
            ["fewer than 300 lines or you need most of it", "normally once per session", "Batch known ranges in one command"],
            ["Reread printed lines only after you edit the file", "or a build or test reports a line in it"]
        ];

        var outputTemplate = Array.IndexOf(lines, "WORKER_RESULT:");
        Assert.True(outputTemplate >= 0);
        var previousStep = -1;
        var procedureCharacters = 0;
        foreach (var fragments in steps)
        {
            if (!includesProcedure)
            {
                Assert.All(fragments, fragment => Assert.DoesNotContain(fragment, brief, StringComparison.Ordinal));
                continue;
            }

            var step = Array.FindIndex(lines, line => line.Contains(fragments[0], StringComparison.Ordinal));
            Assert.True(step > previousStep, $"Read procedure step missing or out of order: {fragments[0]}");
            Assert.True(step < outputTemplate);
            Assert.All(fragments, fragment => Assert.Contains(fragment, lines[step], StringComparison.Ordinal));
            procedureCharacters += lines[step].Length + Environment.NewLine.Length;
            previousStep = step;
        }

        Assert.Equal(contextBudget + procedureCharacters,
            AgentOrchestratorKernel.TaskBriefCharacterBudget(role, usesFileAccessContext: true));
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
