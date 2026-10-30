using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class EnvironmentContractDeriver
{
    public static EnvironmentContract ForModel(ProjectModel model, AppliedOwnerAnswers applied)
    {
        // Engine timeouts do not participate in tool comparison; derive the same check declarations as emission.
        var checks = new List<Dictionary<string, object?>>();
        void Add(string? command, string type, IReadOnlyList<string> arguments) =>
            checks.Add(new() { ["command"] = command, ["type"] = type, ["arguments"] = arguments });
        switch (model.Stack)
        {
            case ProjectStack.Declared:
                foreach (var check in DeclaredProjectDiscoveryAdapter.For(model, applied).Checks) Add(check.Command, check.Type, check.Arguments);
                break;
            case ProjectStack.Crosswalk: Add("crosswalk", "crosswalk-test", []); break;
            case ProjectStack.Go:
                foreach (var check in GoVerificationChecks.Entries) Add(check.Command, "command", check.Arguments);
                break;
            case ProjectStack.Node:
                foreach (var check in NodeVerificationChecks.For(model).Checks) Add(check.Command, check.Type, check.Arguments);
                break;
            case ProjectStack.Lua:
                foreach (var check in LuaProjectDiscoveryAdapter.For(model, applied).Checks) Add(check.Command, check.Type, check.Arguments);
                break;
            default: Add("dotnet", "dotnet-test", []); break;
        }
        return Derive(JsonSerializer.SerializeToElement(checks), model, applied);
    }
    // Cargo's built-ins share its executable; external subcommands need their own availability probe.
    private static readonly HashSet<string> CargoBuiltins = new(StringComparer.Ordinal)
    { "build", "check", "clean", "doc", "fetch", "fix", "generate-lockfile", "help", "info", "init", "install",
      "locate-project", "login", "logout", "metadata", "new", "owner", "package", "pkgid", "publish", "read-manifest",
      "remove", "report", "run", "search", "test", "tree", "uninstall", "update", "vendor", "verify-project", "version", "yank", "add" };

    public static EnvironmentContract Derive(JsonElement checks, ProjectModel? model = null, AppliedOwnerAnswers? answers = null)
    {
        var tools = new Dictionary<string, EnvironmentToolRequirement>(StringComparer.OrdinalIgnoreCase);
        void Add(string command, string source, IReadOnlyList<string>? prefix = null, string? name = null)
        {
            var arguments = (prefix ?? []).Concat(new[] { command == "go" ? "version" : "--version" }).ToArray();
            var key = name ?? command;
            tools.TryAdd(key, new(key, command, arguments, Variables: command == "crosswalk"
                ? ["MCG_ORCHESTRATOR_DATA", "LOCALAPPDATA"] : [], Source: source));
        }
        foreach (var check in checks.EnumerateArray())
        {
            var type = check.TryGetProperty("type", out var kind) ? kind.GetString() : null;
            var command = check.TryGetProperty("command", out var executable) && executable.ValueKind == JsonValueKind.String ? executable.GetString() : type switch
            { "dotnet-test" or "dotnet-build" => "dotnet", "crosswalk-test" => "crosswalk", _ => null };
            if (string.IsNullOrWhiteSpace(command)) continue;
            Add(command, "check");
            if (Path.GetFileNameWithoutExtension(command).Equals("cargo", StringComparison.OrdinalIgnoreCase) &&
                check.TryGetProperty("arguments", out var args) && args.GetArrayLength() > 0 &&
                args[0].GetString() is { } sub && !sub.StartsWith('-') && !CargoBuiltins.Contains(sub))
                Add(command, "check", [sub], "cargo " + sub);
        }
        foreach (var need in model?.EnvironmentNeeds ?? [])
        {
            var command = need.Name switch
            {
                "dotnet-sdk" => "dotnet",
                "go-toolchain" or "go-toolchain-pin" => "go",
                "lua-test-framework" or "crosswalk-test-framework" => StackTool(need.Value.Value),
                "crosswalk-stack" or "crosswalk-list-outcome" or "crosswalk-evidence" or "crosswalk-test-count" or "crosswalk-test-command" or
                "lua-test-command" or "lua-install-command" or "tool-manifest" => null,
                _ => need.Name
            };
            if (!string.IsNullOrWhiteSpace(command)) Add(command, "environment-need:" + need.Name);
        }
        var contract = OwnerAnsweredEnvironmentTools.Overlay(new(1, tools.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray()), answers);
        contract.Validate();
        return contract;
    }

    private static string? StackTool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "" or "none" or "unknown" or "undetermined" => null,
        "pytest" or "unittest" => "python",
        "vitest" or "jest" or "mocha" => "node",
        "junit" => "java",
        _ => value
    };
}
