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
            if (need.Name is "lua-test-framework" or "crosswalk-test-framework")
            {
                if (StackTool(need.Value.Value) is { } probe)
                    Add(probe.Command, "environment-need:" + need.Name, probe.Prefix, probe.Name);
                continue;
            }
            var command = need.Name switch
            {
                "dotnet-sdk" => "dotnet",
                "go-toolchain" or "go-toolchain-pin" => "go",
                NodeVerificationChecks.PackageManagerFact => need.Value.Value is "npm" or "pnpm" or "yarn" ? need.Value.Value : null,
                NodeVerificationChecks.FrameworkFact => need.Value.Value is "vitest" or "jest" or "mocha" ? "node" : null,
                // Needs also describe configuration, evidence and local .NET package IDs. None of
                // those names establishes a PATH executable; owners can declare additional tools.
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(command)) Add(command, "environment-need:" + need.Name);
        }
        var contract = OwnerAnsweredEnvironmentTools.Overlay(new(1, tools.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray()), answers);
        contract.Validate();
        return contract;
    }

    internal static string? UnprobedFramework(string value)
    {
        var framework = value.Trim();
        return framework.ToLowerInvariant() is "" or "none" or "unknown" or "undetermined" || StackTool(framework) is not null
            ? null : framework;
    }

    // Caller vocabulary: LuaProjectDiscoveryAdapter.cs:26-27 emits busted or undetermined;
    // CrosswalkStackReader.cs:48-53 accepts an OPEN set, forwarded by
    // CrosswalkProjectDiscoveryAdapter.cs:29 (absent => empty). none/unknown are legacy sentinels.
    // Reviewed standalone executables: busted, gradle. Mapped: nextest => cargo nextest;
    // maven => mvn (see docs/junit-command-environment.md:14); pytest/unittest => python;
    // vitest/jest/mocha => node; junit => java. These runtime probes retain the existing
    // library/package mappings. All names except Lua's two values come from the open
    // crosswalk input; empty/none/unknown/undetermined have no probe. Other names are unreviewed.
    private static (string Command, IReadOnlyList<string>? Prefix, string? Name)? StackTool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "busted" => ("busted", null, null),
        "gradle" => ("gradle", null, null),
        "nextest" => ("cargo", ["nextest"], "cargo nextest"),
        "maven" => ("mvn", null, null),
        "pytest" or "unittest" => ("python", null, null),
        "vitest" or "jest" or "mocha" => ("node", null, null),
        "junit" => ("java", null, null),
        _ => null
    };
}
