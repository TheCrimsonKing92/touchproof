namespace Mcg.AgentOrchestrator.Core;

/// <summary>
/// Assumes a test class is named after its .cs file. Use the candidate only as a fallback
/// when a declaration, discovered case, or structured source location supplies no identity.
/// </summary>
public static class TestIdentityFilenameAssumption
{
    public static string? GetClassNameCandidate(string? path)
    {
        if (path is null || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return null;

        var stem = Path.GetFileNameWithoutExtension(path);
        return stem.Length == 0 ? null : stem;
    }
}
