using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: guidance and refusal inputs are local immutable values and in-memory kernels.
public sealed class PlannerGateTestSelectorFormTests
{
    private const string Prefix = "Gate tests: owner=acceptance-gate; non-empty tests array of ";
    private const string Suffix = ". Selectorless acceptance: authorized explicit answer; Operator checks stay independent. ";

    [Fact]
    public void DotnetStack_GateTestsSentence_MatchesBaseline()
    {
        // Independent pre-change literal, including its trailing space.
        const string baseline = "Gate tests: owner=acceptance-gate; non-empty tests array of class, Class.Method or exact theory rows. Selectorless acceptance: authorized explicit answer; Operator checks stay independent. ";
        Assert.Equal(baseline, Sentence(AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Planner)[0]));
        Assert.Equal(baseline, Sentence(Directive(ProjectStack.Dotnet)));
        Assert.Equal(baseline, Sentence(Brief(ProjectStack.Dotnet)));
    }

    [Fact]
    public void CargoTestCheck_GateTestsSentence_UsesRunnerIdentity()
    {
        foreach (var command in new[] { "cargo", @"C:\tools\CARGO.exe" })
        {
            var sentence = Sentence(Brief(ProjectStack.Declared, [command]));
            Assert.Contains("exactly as the test runner reports", sentence);
            Assert.Contains("crate::module_path::test_name", sentence);
            Assert.DoesNotContain("Class.Method", sentence);
        }
    }

    [Fact]
    public void JavaScriptTestCheck_GateTestsSentence_UsesRunnerIdentity()
    {
        foreach (var command in new[] { "npm", "npx", "pnpm", "yarn", "bun", "vitest", "jest" })
        {
            var sentence = Sentence(Brief(ProjectStack.Declared, [command]));
            Assert.Contains("exactly as the test runner reports", sentence);
            Assert.Contains("test_file::test_name", sentence);
            Assert.DoesNotContain("Class.Method", sentence);
            Assert.DoesNotContain("crate::", sentence);
        }
    }

    [Fact]
    public void UnknownStack_GateTestsSentence_StatesBothForms()
    {
        var outputs = new[]
        {
            Brief(null), Brief(null, ["cargo"]), Brief(ProjectStack.Go),
            Brief(ProjectStack.Declared), Brief(ProjectStack.Declared, ["wrapper"]),
            Brief(ProjectStack.Declared, ["cargo", "npm"]), Brief(ProjectStack.Declared, ["cargo", null])
        };
        foreach (var output in outputs)
        {
            var sentence = Sentence(output);
            const string dotnet = "on .NET, use the class or Namespace.Class.Method";
            const string declared = "test identities use <test file>::<test name> or the bare test name";
            Assert.Contains(dotnet, sentence);
            Assert.Contains(declared, sentence);
            Assert.True(sentence.IndexOf(dotnet, StringComparison.Ordinal) < sentence.IndexOf(declared, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Refusal_EachStack_SharesDirectiveFormText()
    {
        Check(ProjectStack.Dotnet, null, [new("tests", _ => false, "dotnet-test")]);
        Check(ProjectStack.Declared, ["cargo"], [new("tests", _ => false, "junit-command", "cargo")]);
        Check(ProjectStack.Declared, ["npx"], [new("tests", _ => false, "junit-command", "npx")]);
        Check(null, null, []);
        Check(ProjectStack.Declared, ["cargo", "npm"],
            [new("rust", _ => false, "junit-command", "cargo"), new("js", _ => false, "junit-command", "npm")]);

        static void Check(ProjectStack? stack, string?[]? commands, DeclaredGateCheck[] checks)
        {
            var sentence = Sentence(Directive(stack, commands));
            var form = sentence[Prefix.Length..^Suffix.Length];
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
            var goal = kernel.CreateGoal("Choose evidence", [task]);
            var refusal = PlannerCommandSelectorValidation.Refusal(["npm unmatched"], goal, task, (_, _) => checks);
            Assert.NotNull(refusal);
            Assert.Contains(form, refusal);
            if (commands is ["cargo"])
                Assert.DoesNotContain("Namespace.Class.Method", refusal);
        }
    }

    [Fact]
    public void Refusal_MixedCheckTypes_UsesUnknownForms()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
        var goal = kernel.CreateGoal("Choose evidence", [task]);
        var refusal = PlannerCommandSelectorValidation.Refusal(["npm unmatched"], goal, task,
            (_, _) => [new("dotnet", _ => false, "dotnet-test"), new("rust", _ => false, "junit-command", "cargo")]);
        Assert.NotNull(refusal);
        Assert.Contains("on .NET, use the class or Namespace.Class.Method", refusal);
        Assert.Contains("test identities use <test file>::<test name> or the bare test name", refusal);
    }

    private static string Directive(ProjectStack? stack, string?[]? commands = null) =>
        AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Planner, stack: stack, testCheckCommands: commands)[0];

    private static string Brief(ProjectStack? stack, string?[]? commands = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Plan test evidence", AgentRole.Planner);
        var goal = kernel.CreateGoal("Choose selectors", [task]);
        return kernel.BuildTaskBriefSource(goal.Id, task.Id, roleRequirementStack: stack, testCheckCommands: commands)
            .ProjectLegacyMarkedTextV1().Content;
    }

    private static string Sentence(string output)
    {
        var start = output.IndexOf(Prefix, StringComparison.Ordinal);
        Assert.True(start >= 0, "Planner gate-tests sentence was not emitted.");
        var end = output.IndexOf(Suffix, start, StringComparison.Ordinal);
        Assert.True(end >= start, "Planner gate-tests sentence was not terminated.");
        return output[start..(end + Suffix.Length)];
    }
}
