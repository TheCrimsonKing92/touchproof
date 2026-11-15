using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mcg.SemanticImpactSpike;

public sealed record QueryDefinition
{
    public required string Id { get; init; }
    public int OperatorNumber { get; init; }
    public required string Kind { get; init; }
    public required string Project { get; init; }
    public string[] Targets { get; init; } = [];
    public string? File { get; init; }
    public string[] Sites { get; init; } = [];
    public string Mode { get; init; } = "exact";
    public string[] Expected { get; init; } = [];
    public string[] Excluded { get; init; } = [];
    public required string Evidence { get; init; }
    public bool DecisionRequired { get; init; }
}

public sealed record QueryData(string BaselineRevision, string SourceTree, string TestsTree, QueryDefinition[] Queries)
{
    public int SchemaVersion { get; init; } = 1;
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static QueryData Read(string path) => JsonSerializer.Deserialize<QueryData>(System.IO.File.ReadAllText(path), Json)!;
    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Read(path), Json)));
}

public sealed record QueryResult(string Id, string Score, string[] Returned, string[] Missing, string[] Extra,
    string[] Unconstrained, double Seconds, string? Error = null);

public sealed class ImpactQueryEngine(ReverseReferenceIndex index)
{
    public QueryResult Execute(QueryDefinition query, bool frameworkDispatch = false)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var targets = query.File is not null
                ? index.Files.GetValueOrDefault(query.File)?.ToArray() ?? throw new InvalidOperationException("No declarations in " + query.File)
                : query.Targets.Select(id => query.Project + "|" + id).ToArray();
            foreach (var target in targets)
                if (!index.Symbols.ContainsKey(target)) throw new InvalidOperationException("Unresolved symbol: " + target);
            string[] actual = query.Kind switch
            {
                "affected" => index.Affected(targets, frameworkDispatch),
                "calls" => index.Sites.Where(s => targets.Contains(s.Target) && s.Kind == "call")
                    .Select(s => s.Path + ":" + s.Line).Distinct().Order(StringComparer.Ordinal).ToArray(),
                "callers" => index.Sites.Where(s => targets.Contains(s.Target) && s.Kind == "call")
                    .Select(s => CallerDeclaration(s.Caller)).Distinct().Order(StringComparer.Ordinal).ToArray(),
                "implementers" => targets.SelectMany(t => index.ImplementingTypes.GetValueOrDefault(t) ?? [])
                    .Select(k => k.Split('|')[1]).Distinct().Order(StringComparer.Ordinal).ToArray(),
                "dispatch" => index.Sites.Where(s => query.Sites.Contains(s.Path + ":" + s.Line))
                    .SelectMany(s => (index.Implementations.GetValueOrDefault(s.Target) ?? [s.Target])
                        .Select(impl => s.Path + ":" + s.Line + " => " + impl)).Distinct().Order(StringComparer.Ordinal).ToArray(),
                "binding" => index.Sites.Where(s => query.Sites.Contains(s.Path + ":" + s.Line))
                    .Select(s => s.Target).Distinct().Order(StringComparer.Ordinal).ToArray(),
                "construction" => index.Sites.Where(s => targets.Contains(s.Target) && s.Kind == "construction")
                    .Select(s => s.Path + ":" + s.Line).Distinct().Order(StringComparer.Ordinal).ToArray(),
                "tests" => targets.SelectMany(t => index.TestMethods.GetValueOrDefault(t) ?? [])
                    .Distinct().Order(StringComparer.Ordinal).ToArray(),
                "delegate" => index.DelegateSupplies.Where(s => targets.Contains(s.Target) && s.Parameter == "crosswalkReaderFactory")
                    .SelectMany(s => index.Affected([s.Caller]).Append(s.ReachedType)).Distinct().Order(StringComparer.Ordinal).ToArray(),
                _ => throw new InvalidOperationException("Unknown query kind: " + query.Kind)
            };
            return Compare(query, actual, timer.Elapsed.TotalSeconds);
        }
        catch (InvalidOperationException error)
        {
            return new(query.Id, query.Mode == "no ground truth" ? "unscored" : "wrong", [], query.Expected, [], [], timer.Elapsed.TotalSeconds, error.Message);
        }
    }

    private string CallerDeclaration(string key)
    {
        var location = index.Symbols[key].Locations.FirstOrDefault(l => l.IsInSource)
            ?? throw new InvalidOperationException("No caller source: " + key);
        var file = location.SourceTree!.FilePath;
        var path = index.Files.Keys.FirstOrDefault(p => file.Replace('\\', '/').EndsWith('/' + p, StringComparison.OrdinalIgnoreCase));
        return path + ":" + (location.GetLineSpan().StartLinePosition.Line + 1);
    }

    public static QueryResult Compare(QueryDefinition query, string[] actual, double seconds = 0)
    {
        var missing = query.Expected.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var extra = (query.Mode == "required/excluded" ? actual.Intersect(query.Excluded, StringComparer.Ordinal)
            : actual.Except(query.Expected, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        var score = query.Mode == "no ground truth" ? "unscored"
            : missing.Length == 0 && extra.Length == 0 ? "correct"
            : actual.Intersect(query.Expected, StringComparer.Ordinal).Any() ? "partial" : "wrong";
        return new(query.Id, score, actual, missing, extra,
            query.Mode == "required/excluded" ? actual.Except(query.Expected, StringComparer.Ordinal).Except(query.Excluded, StringComparer.Ordinal).ToArray() : [], seconds);
    }
}
