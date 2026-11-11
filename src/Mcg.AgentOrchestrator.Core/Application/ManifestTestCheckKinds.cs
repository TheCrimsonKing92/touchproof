namespace Mcg.AgentOrchestrator.Core;

/// <summary>The test kinds already executable by the acceptance gate and their plan commands.</summary>
public static class ManifestTestCheckKinds
{
    public const string Dotnet = "dotnet-test";
    public const string Crosswalk = "crosswalk-test";

    public static string? Canonicalize(string? kind) =>
        string.Equals(kind, Dotnet, StringComparison.OrdinalIgnoreCase) ? Dotnet :
        string.Equals(kind, Crosswalk, StringComparison.OrdinalIgnoreCase) ? Crosswalk : null;

    public static string? FromCommand(IReadOnlyList<string> command) =>
        command.Count > 0 && command[0].Equals(Crosswalk, StringComparison.OrdinalIgnoreCase) ? Crosswalk :
        command.Count > 1 && command[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
        command[1].Equals("test", StringComparison.OrdinalIgnoreCase) ? Dotnet : null;

    public static IReadOnlyList<string> Command(string kind, string? project, IReadOnlyList<string> arguments) =>
        Canonicalize(kind) switch
        {
            Dotnet => ["dotnet", "test", "--project", project!, .. arguments],
            Crosswalk => [Crosswalk, string.IsNullOrWhiteSpace(project) ? "." : project, .. arguments],
            _ => throw new ArgumentException($"Unsupported test check kind: {kind}", nameof(kind))
        };

    public static string Describe(IEnumerable<string> kinds)
    {
        var found = kinds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", new[] { Dotnet, Crosswalk }.Where(found.Contains));
    }
}
