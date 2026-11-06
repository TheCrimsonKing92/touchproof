using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ProjectEnvironmentResolution(
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> ToolVariables,
    IReadOnlyList<string> Missing,
    string? Error = null);

internal sealed class ProjectEnvironmentResolver(
    Func<IReadOnlyDictionary<string, string>> readMachine,
    Func<IReadOnlyDictionary<string, string>> readUser,
    Func<string, bool>? fileExists = null,
    Func<IReadOnlyDictionary<string, string>>? readSystemDefined = null)
{
    // Windows supplies these outside the persisted tool settings. Never import arbitrary
    // process variables: a stale launching shell must not satisfy a tool requirement.
    private static readonly string[] SystemDefinedNames =
    [
        "SystemRoot", "windir", "SystemDrive", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "USERPROFILE",
        "APPDATA", "LOCALAPPDATA", "USERNAME", "USERDOMAIN", "USERDOMAIN_ROAMINGPROFILE",
        "COMPUTERNAME", "HOMEDRIVE", "HOMEPATH", "ALLUSERSPROFILE", "ProgramData", "PUBLIC"
    ];

    public ProjectEnvironmentResolution Resolve(IReadOnlyList<ProjectToolRequirement> requirements)
    {
        requirements = ProjectToolRequirement.Validate(requirements);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var systemDefined = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (readSystemDefined is not null)
            {
                var supplied = new Dictionary<string, string>(readSystemDefined(), StringComparer.OrdinalIgnoreCase);
                foreach (var name in SystemDefinedNames)
                    if (supplied.TryGetValue(name, out var value)) systemDefined[name] = value;
            }
            foreach (var pair in readMachine()) environment[pair.Key] = pair.Value;
            foreach (var pair in readUser())
            {
                environment[pair.Key] = pair.Key.Equals("Path", StringComparison.OrdinalIgnoreCase) &&
                    environment.TryGetValue(pair.Key, out var machinePath)
                    ? machinePath + Path.PathSeparator + pair.Value : pair.Value;
            }
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            return new(environment, variables, requirements.Select(MissingName).ToArray(), ex.Message);
        }

        // System-defined values supply expansion context and the current Windows identity,
        // overriding machine defaults such as USERNAME=SYSTEM. Tool settings stay scope-only.
        string Expand(string name, HashSet<string> visiting)
        {
            if ((!systemDefined.TryGetValue(name, out var value) && !environment.TryGetValue(name, out value)) ||
                !visiting.Add(name)) return "%" + name + "%";
            var expanded = Regex.Replace(value, "%([^%]+)%", match => Expand(match.Groups[1].Value, visiting));
            visiting.Remove(name);
            return expanded;
        }
        var expanded = environment.Keys.ToDictionary(name => name,
            name => Expand(name, new(StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
        if (expanded.TryGetValue("Path", out var path))
        {
            expanded["Path"] = string.Join(Path.PathSeparator, path.Split(Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(entry => !Regex.IsMatch(entry, "%[^%]+%")));
            if (expanded["Path"].Length == 0 && Regex.IsMatch(path, "%[^%]+%")) expanded.Remove("Path");
        }
        // Do not overwrite a valid inherited value with an unresolved registry expression.
        foreach (var name in expanded.Where(pair => Regex.IsMatch(pair.Value, "%[^%]+%"))
                     .Select(pair => pair.Key).ToArray()) expanded.Remove(name);

        var missing = new List<string>();
        foreach (var requirement in requirements)
        {
            if (requirement.EnvironmentVariable is { } variable)
            {
                if (expanded.TryGetValue(variable, out var value) && !string.IsNullOrWhiteSpace(value) &&
                    !Regex.IsMatch(value, "%[^%]+%")) variables[variable] = value;
                else missing.Add(MissingName(requirement));
            }
            else if (!ToolOnPath(requirement.Tool, expanded)) missing.Add(requirement.Tool);
        }
        return new(expanded, variables, missing);
    }

    private bool ToolOnPath(string tool, IReadOnlyDictionary<string, string> environment)
    {
        if (!environment.TryGetValue("Path", out var path)) return false;
        var extensions = environment.TryGetValue("PATHEXT", out var value) ? value : ".COM;.EXE;.BAT;.CMD";
        var names = Path.HasExtension(tool) ? new[] { tool } :
            new[] { tool }.Concat(extensions.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(ext => tool + ext));
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(directory => names.Any(name => (fileExists ?? File.Exists)(Path.Combine(directory.Trim('"'), name))));
    }

    private static string MissingName(ProjectToolRequirement requirement) =>
        requirement.EnvironmentVariable is { } variable ? $"{requirement.Tool} ({variable})" : requirement.Tool;

    public static ProjectEnvironmentResolver CreateSystem() => new(
        () => ReadScope(machine: true), () => ReadScope(machine: false),
        readSystemDefined: ReadSystemDefined);

    private static IReadOnlyDictionary<string, string> ReadSystemDefined()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SystemDefinedNames)
            if (Environment.GetEnvironmentVariable(name) is { } value) values[name] = value;
        return values;
    }

    private static IReadOnlyDictionary<string, string> ReadScope(bool machine)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return values;
        using var key = machine
            ? Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment")
            : Registry.CurrentUser.OpenSubKey("Environment");
        if (key is null) return values;
        foreach (var name in key.GetValueNames())
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
                values[name] = value;
        return values;
    }
}
