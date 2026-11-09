namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class RepositoryScopeIdentityComparer : IEqualityComparer<(string RepositoryKey, string Path)>
{
    public static RepositoryScopeIdentityComparer Instance { get; } = new();

    public bool Equals((string RepositoryKey, string Path) left, (string RepositoryKey, string Path) right) =>
        StringComparer.Ordinal.Equals(left.RepositoryKey, right.RepositoryKey) &&
        StringComparer.OrdinalIgnoreCase.Equals(left.Path, right.Path);

    public int GetHashCode((string RepositoryKey, string Path) value) => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(value.RepositoryKey),
        StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path));
}
