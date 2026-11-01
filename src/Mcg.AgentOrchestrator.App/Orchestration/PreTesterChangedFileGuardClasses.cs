using Mcg.AgentOrchestrator.Core;
using System.Text.RegularExpressions;
using GuardReference = Mcg.AgentOrchestrator.App.Orchestration.PreTesterGuardReferenceReader.GuardReference;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One instance belongs to one repository's driver. Only unchanged test sources enter the cache;
// candidate edits are overlaid per selection and never become another candidate's baseline.
internal sealed class PreTesterChangedFileGuardClasses
{
    internal sealed record Provenance(string Class, string Kind, string Path);
    private readonly object _sync = new();
    private string? _revision;
    private Dictionary<string, GuardReference[]> _sources = new(StringComparer.OrdinalIgnoreCase);

    internal DeveloperDeferredTestSelection Select(
        string worktreePath, IReadOnlyList<string> changedPaths, string mainRevision)
        => SelectWithProvenance(worktreePath, changedPaths, mainRevision).Selection;

    internal (DeveloperDeferredTestSelection Selection, IReadOnlyList<Provenance> Provenance) SelectWithProvenance(
        string worktreePath, IReadOnlyList<string> changedPaths, string mainRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainRevision);
        if (changedPaths.Count == 0) return (new([], []), []);
        var changed = changedPaths.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(worktreePath, "tests");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        string[] names;
        var provenance = new List<Provenance>();
        lock (_sync)
        {
            // Publish a complete generation only after every source was read successfully.
            var sources = string.Equals(_revision, mainRevision, StringComparison.Ordinal)
                ? new Dictionary<string, GuardReference[]>(_sources, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GuardReference[]>(StringComparer.OrdinalIgnoreCase);
            var references = new List<GuardReference>();
            foreach (var file in DeveloperDeferredTestSelections.EnumerateSourceFiles(root)
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                var relative = Normalize(Path.GetRelativePath(worktreePath, file));
                if (changed.Contains(relative))
                    references.AddRange(ReadReferences(file));
                else
                {
                    if (!sources.TryGetValue(relative, out var cached))
                        sources[relative] = cached = ReadReferences(file);
                    references.AddRange(cached);
                }
            }
            names = references.Where(reference => changed.Any(path => MatchesReference(path, reference)))
                .Select(reference => reference.Class).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var name in names)
            {
                foreach (var path in changedPaths)
                {
                    var match = references.Where(reference => reference.Class == name &&
                            MatchesReference(Normalize(path), reference))
                        .OrderBy(reference => reference.Kind switch { "read" => 0, "helper-read" => 1, _ => 2 })
                        .FirstOrDefault();
                    if (match is null) continue;
                    provenance.Add(new(name, match.Kind, path));
                    break;
                }
            }
            _sources = sources;
            _revision = mainRevision;
        }
        // Resolution is candidate-specific, even when the reference map is cached.
        return (DeveloperDeferredTestSelections.ResolveNames(worktreePath, names, requireUniqueSourceFile: true), provenance);
    }

    internal static bool ContainsClass(IReadOnlyList<FindingEvidenceSelection> selections, string? testClass) =>
        selections.Any(selection => string.Equals(selection.TestClass, testClass, StringComparison.Ordinal));

    private static bool MatchesReference(string changed, GuardReference reference)
    {
        if (reference.Kind != "scan") return Matches(changed, reference.Path);
        var prefix = string.Join('/', reference.Path) + "/";
        if (!changed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var remainder = changed[prefix.Length..];
        if (remainder.Length == 0 || (!reference.Recursive && remainder.Contains('/'))) return false;
        var filename = remainder[(remainder.LastIndexOf('/') + 1)..];
        var pattern = "\\A" + Regex.Escape(reference.Pattern).Replace("\\*", ".*").Replace("\\?", ".") + "\\z";
        return Regex.IsMatch(filename, pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    private static bool Matches(string changed, string[] segments)
    {
        var path = string.Join('/', segments);
        return changed.Equals(path, StringComparison.OrdinalIgnoreCase) ||
               changed.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase);
    }

    internal static string Normalize(string path) =>
        string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != "."));

    private static GuardReference[] ReadReferences(string file) => PreTesterGuardReferenceReader.Read(file);
}
