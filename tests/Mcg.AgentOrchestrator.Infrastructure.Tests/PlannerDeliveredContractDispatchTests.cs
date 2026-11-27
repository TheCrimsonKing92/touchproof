using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Xunit.Sdk;

// Dispatch preparation uses an independently owned repository and does not launch a worker.
public sealed class PlannerDeliveredContractDispatchTests : WorkerDispatchTestSupport
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Initial_round_delivers_batch_contract_through_prepared_rules_file(bool typed)
    {
        var root = CreateSeededDispatchRepository();
        var (kernel, goal, task) = CreatePlannerTask();

        var prepared = PreparePlanner(kernel, goal, task, root, typed);
        var prompt = File.ReadAllText(prepared.PromptPath);

        AssertDeliveredPlannerContract(ResolveDeliveredRulesPath(prepared, prompt, root, goal, typed));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Retry_round_after_contract_rejection_delivers_batch_contract(bool typed)
    {
        const string rejectedPlan = "A plan with malformed evidence directives.";
        const string rejectionRule = "planner-output-contract-rejected";
        const string retryFeedback = "Repair the evidence directive formatting.";
        var root = CreateSeededDispatchRepository();
        var (kernel, goal, task) = CreatePlannerTask();
        kernel.RecordTaskVerification(goal.Id, task.Id,
            new TaskVerificationRecord("worker", root, 1, rejectedPlan, "contract rejection",
                DateTimeOffset.UtcNow.AddMinutes(-1), FullStandardOutput: rejectedPlan,
                CompletionVerdictRule: rejectionRule));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Planner contract rejected.");
        kernel.RetryTaskWithAuthoritativeFeedback(goal.Id, task.Id, retryFeedback, RetryCause.ContractClarification);

        Assert.Null(task.LastVerification);
        Assert.Equal(RetryCause.ContractClarification, task.PendingRetryCause);
        var rejected = Assert.Single(task.VerificationHistory);
        Assert.False(rejected.Succeeded);
        Assert.Equal(rejectionRule, rejected.CompletionVerdictRule);

        var prepared = PreparePlanner(kernel, goal, task, root, typed);
        var prompt = File.ReadAllText(prepared.PromptPath);
        if (typed)
        {
            var rejectedPlanPath = ResolveMandatoryContextPath(prepared, prompt, root, "task/rejected-planner/stdout");
            Assert.Equal(rejectedPlan, File.ReadAllText(rejectedPlanPath));
        }
        else
        {
            Assert.Contains(retryFeedback, prompt, StringComparison.Ordinal);
        }

        AssertDeliveredPlannerContract(ResolveDeliveredRulesPath(prepared, prompt, root, goal, typed));
    }

    [Fact]
    public void Contract_assertion_helper_fails_for_rules_file_without_batch_wording()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var rulesPath = Path.Combine(directory.FullName, WorkerStandingRules.PlannerContextFileName);
            File.WriteAllLines(rulesPath,
                [AgentOutputDirectives.PlannerStandingRules, AgentOutputDirectives.PlannerResponseValidation]);

            Assert.Throws<ContainsException>(() => AssertDeliveredPlannerContract(rulesPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) CreatePlannerTask()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Plan the label update.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Maintain the label.", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return (kernel, goal, task);
    }

    private static WorkerProfileDispatchResult PreparePlanner(
        AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string root, bool typed) =>
        WorkerProfileDispatcher.PrepareTask(
            kernel, goal, task,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(root, "prompts"), root, DateTimeOffset.UtcNow,
            providerName: typed ? "OpenAI" : "Anthropic",
            modelName: typed ? AgentCatalog.OpenAiSolSubscriptionModelAlias : "claude-sonnet-4-6");

    private static string ResolveDeliveredRulesPath(
        WorkerProfileDispatchResult prepared, string prompt, string root, Goal goal, bool typed)
    {
        var fileName = WorkerStandingRules.PlannerContextFileName;
        if (typed)
        {
            return ResolveMandatoryContextPath(prepared, prompt, root, $"context/{fileName}");
        }

        Assert.Null(prepared.Task.LastDispatch!.ContextPackageReceipt);
        var contextDirectory = Path.Combine(root, ".orchestrator-context", goal.Id.Value);
        Assert.Contains(Path.Combine(contextDirectory, "digest.md"), prompt, StringComparison.Ordinal);
        Assert.Contains($"Read {fileName} in the context directory", prompt, StringComparison.Ordinal);
        return Path.Combine(contextDirectory, fileName);
    }

    private static string ResolveMandatoryContextPath(
        WorkerProfileDispatchResult prepared, string prompt, string root, string identity)
    {
        var receipt = prepared.Task.LastDispatch!.ContextPackageReceipt;
        Assert.NotNull(receipt);
        var section = Assert.Single(receipt.Sections, section => section.LogicalIdentity == identity);
        Assert.Equal(ContextDeliveryMode.MandatoryFile, section.DeliveryMode);
        var path = section.MandatoryRelativePath;
        Assert.NotNull(path);
        var reference = Assert.Single(prompt.Split('\n'), line =>
            line.StartsWith($"MANDATORY READ: identity={identity};", StringComparison.Ordinal));
        Assert.Contains($"path={path};", reference, StringComparison.Ordinal);
        return Path.Combine(root, path);
    }

    private static void AssertDeliveredPlannerContract(string rulesPath)
    {
        var text = File.ReadAllText(rulesPath);
        Assert.Contains(AgentOutputDirectives.PlannerResponseFormat, text, StringComparison.Ordinal);
        Assert.Contains(AgentOutputDirectives.PlannerResponseValidation, text, StringComparison.Ordinal);
    }
}
