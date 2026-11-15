using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns a temporary worktree, manifest, project home and kernel.
public sealed class PlannerGateTestSelectorFormRoutingTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("selector-form-");

    [Theory]
    [InlineData("cargo", "test", "crate::module_path::test_name")]
    [InlineData("npx", "vitest", "test_file::test_name")]
    public void DeclaredManifest_BriefAndCurrentTask_UseRunnerForm(string command, string argument, string example)
    {
        var worktree = Directory.CreateDirectory(Path.Combine(_root.FullName, "worktree")).FullName;
        var home = Directory.CreateDirectory(Path.Combine(_root.FullName, "home")).FullName;
        File.WriteAllText(Path.Combine(home, "project-model.json"), """{"stack":"declared"}""");
        var manifestPath = Path.Combine(home, "acceptance-manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            checks = new[] { new { name = "declared-test", type = "junit-command", command, arguments = new[] { argument } } }
        }));
        var targetHome = new WorkerTargetHome(false, _ => manifestPath, home);
        var stack = ProjectStackResolver.GuidanceStackFor(worktree, targetHome);
        Assert.Equal(ProjectStack.Declared, stack);
        var commands = ProjectStackResolver.TestCheckCommandsFor(worktree, targetHome, stack);
        Assert.Equal(command, Assert.Single(commands));

        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Plan test evidence", AgentRole.Planner);
        var goal = kernel.CreateGoal("Choose runner selectors", [task]);
        var brief = kernel.BuildTaskBriefSource(goal.Id, task.Id, roleRequirementStack: stack, testCheckCommands: commands)
            .ProjectLegacyMarkedTextV1(emitTypedSourceBoundaries: false).Content;
        var context = WorkerContextArtifacts.Write(goal, task, worktree, targetHome: targetHome);
        var currentTask = File.ReadAllText(Path.Combine(context, "current-task.md"));

        foreach (var output in new[] { brief, currentTask })
        {
            var form = Form(output);
            Assert.Contains("exactly as the test runner reports", form);
            Assert.Contains(example, form);
            Assert.DoesNotContain("Class.Method", form);
        }
        Assert.Equal(Form(brief), Form(currentTask));

        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("planner", "plan", worktree, DateTimeOffset.UtcNow));
        var checks = DeclaredGateCheckSource.For(home)(goal, task);
        var check = Assert.Single(checks);
        Assert.Equal("junit-command", check.Type);
        Assert.Equal(command, check.Command);
        var refusal = PlannerCommandSelectorValidation.Refusal(["npm unmatched"], goal, task, (_, _) => checks);
        Assert.NotNull(refusal);
        Assert.Contains(Form(brief), refusal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("""{"checks":42}""")]
    [InlineData("""{"checks":[{"type":"junit-command","command":42}]}""")]
    [InlineData("""{"checks":[{"type":"command","command":"cargo"}]}""")]
    public void UnavailableTestCommands_Guidance_UsesBothForms(string? manifest)
    {
        var path = Path.Combine(_root.FullName, "manifest.json");
        if (manifest is not null) File.WriteAllText(path, manifest);
        var commands = ProjectStackResolver.TestCheckCommandsFor(_root.FullName,
            new WorkerTargetHome(false, _ => path), ProjectStack.Declared);
        var output = AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Planner,
            stack: ProjectStack.Declared, testCheckCommands: commands)[0];
        var form = Form(output);
        Assert.Contains("on .NET, use the class or Namespace.Class.Method", form);
        Assert.Contains("test identities use <test file>::<test name> or the bare test name", form);
    }

    private static string Form(string output)
    {
        const string prefix = "Gate tests: owner=acceptance-gate; non-empty tests array of ";
        const string suffix = ". Selectorless acceptance:";
        var start = output.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0, "Planner gate-tests sentence was not emitted.");
        start += prefix.Length;
        var end = output.IndexOf(suffix, start, StringComparison.Ordinal);
        Assert.True(end >= start, "Planner gate-tests sentence was not terminated.");
        return output[start..end];
    }

    public void Dispose() => _root.Delete(recursive: true);
}
