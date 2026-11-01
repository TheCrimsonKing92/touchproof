namespace Mcg.AgentOrchestrator.Infrastructure;

// The effective manifest, rather than stack discovery, authorizes this preparation command.
public sealed record NodeDependencyInstall(string Command, IReadOnlyList<string> Arguments)
{
    public static NodeDependencyInstall? FromDeclaredCheck(
        string? type, string? command, IReadOnlyList<string>? arguments)
    {
        if (!string.Equals(type, "command", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(command) || arguments is not { Count: > 0 })
            return null;

        var executable = Path.GetFileName(command.Replace('\\', '/'));
        if (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            executable = executable[..^4];
        var install = arguments[0] == "install";
        var eligible = executable.ToLowerInvariant() switch
        {
            "npm" => install || arguments[0] == "ci",
            "pnpm" or "yarn" or "bun" => install,
            _ => false
        };
        return eligible ? new NodeDependencyInstall(command, arguments.ToArray()) : null;
    }
}
