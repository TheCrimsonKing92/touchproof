namespace Mcg.AgentOrchestrator.Core;

// Guidance conventions only; selector acceptance remains owned by the host matcher.
public static class GateTestSelectorForm
{
    private enum Convention { Unknown, Dotnet, Cargo, JavaScript }

    public static string ForStack(ProjectStack? stack, IReadOnlyList<string?>? testCheckCommands = null) =>
        Text(stack == ProjectStack.Dotnet ? Convention.Dotnet :
            stack == ProjectStack.Declared ? ClassifyCommands(testCheckCommands) : Convention.Unknown);

    internal static string ForChecks(IReadOnlyList<DeclaredGateCheck> checks)
    {
        var dotnet = checks.Any(check => check.Type == "dotnet-test");
        var declared = checks.Where(check => check.Type == "junit-command").ToArray();
        return Text(dotnet && declared.Length == 0 ? Convention.Dotnet :
            !dotnet ? ClassifyCommands(declared.Select(check => check.Command).ToArray()) : Convention.Unknown);
    }

    public static string DirectiveSentence(ProjectStack? stack, IReadOnlyList<string?>? testCheckCommands = null) =>
        "Gate tests: owner=acceptance-gate; non-empty tests array of " + ForStack(stack, testCheckCommands) +
        ". Selectorless acceptance: authorized explicit answer; Operator checks stay independent. ";

    private static string Text(Convention convention) => convention switch
    {
        Convention.Dotnet => "class, Class.Method or exact theory rows",
        Convention.Cargo => "test identities exactly as the test runner reports them, such as crate::module_path::test_name, keeping every :: separator",
        Convention.JavaScript => "test identities exactly as the test runner reports them, such as test_file::test_name, keeping the :: separator",
        _ => "test identities in the form of the project's stack: on .NET, use the class or Namespace.Class.Method; on declared-command stacks, test identities use <test file>::<test name> or the bare test name"
    };

    private static Convention ClassifyCommands(IReadOnlyList<string?>? commands)
    {
        if (commands is null || commands.Count == 0) return Convention.Unknown;
        Convention? result = null;
        foreach (var command in commands)
        {
            // Only the configured executable is evidence; wrappers and aliases are unknown.
            var executable = Path.GetFileNameWithoutExtension(command)?.ToLowerInvariant();
            var current = executable switch
            {
                "cargo" => Convention.Cargo,
                "npm" or "npx" or "pnpm" or "yarn" or "bunx" or "bun" or "node" or "deno" or
                    "vitest" or "jest" or "mocha" => Convention.JavaScript,
                _ => Convention.Unknown
            };
            if (current == Convention.Unknown || result is { } prior && prior != current)
                return Convention.Unknown;
            result = current;
        }
        return result ?? Convention.Unknown;
    }
}
