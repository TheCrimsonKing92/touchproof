using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Rejects cross-stack marker evidence without inspecting repository contents.</summary>
internal static class CrosswalkEvidenceStackAmbiguity
{
    internal static void ThrowIfAmbiguous(string repositoryRoot, string evidenceJson)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(comparer);
        var markers = new List<(int Stack, string Original)>();
        using var evidence = JsonDocument.Parse(evidenceJson);
        foreach (var item in evidence.RootElement.EnumerateArray())
        {
            var original = item.GetProperty("path").GetString()!;
            if (string.IsNullOrWhiteSpace(original)) continue;
            string path;
            try
            {
                path = Path.GetFullPath(original.Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar), root);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!path.StartsWith(prefix, comparison)) continue;
            var relative = path[prefix.Length..];
            var atRoot = !relative.Contains(Path.DirectorySeparatorChar);
            var extension = Path.GetExtension(relative).ToLowerInvariant();
            var stack = atRoot && relative.Equals("go.mod", comparison) ? 0
                : atRoot && relative.Equals("package.json", comparison) ? 1
                : (atRoot && extension is ".sln" or ".slnx") || extension is ".csproj" or ".fsproj" or ".vbproj" ? 2 : -1;
            if (stack >= 0 && seen.Add(path)) markers.Add((stack, original));
        }
        if (markers.Select(marker => marker.Stack).Distinct().Count() > 1)
            throw new InvalidOperationException("No discovery adapter is available for this repository: Ambiguous stack markers: " +
                string.Join(" and ", markers.OrderBy(marker => marker.Stack).Select(marker => marker.Original)) + ".");
    }
}
