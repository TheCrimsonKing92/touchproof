using Mcg.AgentOrchestrator.Core;

// Parallel-safe: builds briefs in memory with no filesystem access.
public sealed class DeveloperReachCheckPromptTests
{
    [Theory]
    [InlineData("Update the label.")]
    [InlineData("Design and implement production architecture and distributed integration.")]
    public void DeveloperBriefIncludesProcedureAndOptionalField(string taskDescription)
    {
        var (api, file) = Briefs(AgentRole.Developer, taskDescription);
        foreach (var brief in new[] { api, file })
        {
            var procedure = Assert.Single(brief.Split('\n'),
                line => line.Contains("search each new/changed", StringComparison.Ordinal));
            foreach (var fragment in new[]
            {
                "Before WORKER_RESULT", "branch predicate", "method signature", "user-visible string/flag",
                "sibling production paths, tests and fixtures", "wt find --references <Name>", "git grep -n -w",
                "where available/applicable", "quote terms", "old/new terms", "literal/token variants",
                "enclosing symbol + tokens for unnamed predicates", "each hit in reach_check by subject, repo-relative path[:line]",
                "updated", "covered-by <test>", "unaffected - <reason>",
                "criteria naming files/packages/symbols/artifacts", "grep or non-empty/absence/tracked-file checks",
                "criteria_self_check"
            })
                Assert.Contains(fragment, procedure, StringComparison.Ordinal);

            var overflow = Assert.Single(brief.Split('\n'),
                line => line.StartsWith("- Over limit,", StringComparison.Ordinal));
            foreach (var fragment in new[]
            {
                "keep each subject", "each updated or sibling production reference", "only same-pattern hits",
                "file/dir path or glob and disposition", "omit reach_check", "overflowed subjects in prose"
            })
                Assert.Contains(fragment, overflow, StringComparison.Ordinal);

            var field = Assert.Single(brief.Split('\n'),
                line => line.StartsWith("reach_check:", StringComparison.Ordinal));
            foreach (var fragment in new[]
            {
                "optional one-line JSON array", "{symbol,reference,disposition}", "100 entries",
                "32768 UTF-8 bytes", "[] allowed", "omit and explain in report prose"
            })
                Assert.Contains(fragment, field, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("reach_check", AgentOutputDirectives.RequiredWorkerResultFieldNamesForRole(AgentRole.Developer));
        Assert.DoesNotContain("reach_check", AgentOutputDirectives.WorkerResultFieldNames);
    }

    [Theory]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Reviewer)]
    public void OtherRoleBriefsOmitDeveloperProcedureAndField(AgentRole role)
    {
        foreach (var description in new[] { "Check the label.", "Design and implement production architecture and distributed integration." })
        {
            var (api, file) = Briefs(role, description);
            foreach (var brief in new[] { api, file })
            {
                Assert.DoesNotContain("wt find --references", brief, StringComparison.Ordinal);
                Assert.DoesNotContain("reach_check:", brief, StringComparison.Ordinal);
            }
        }
    }

    private static (string Api, string File) Briefs(AgentRole role, string description)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), description, role);
        var goal = kernel.CreateGoal("Maintain the label.", [task]);
        return (kernel.BuildTaskBrief(goal.Id, task.Id).Content,
            kernel.BuildTaskBrief(goal.Id, task.Id,
                workingDirectory: "C:\\repo", contextDirectory: "C:\\repo\\context").Content);
    }
}
