namespace Mcg.AgentOrchestrator.Core;

/// <summary>A quoted guidance item, retained with its heading and source rather than reinterpreted.</summary>
public sealed record ProjectConvention
{
    public string DocumentPath { get; }
    public string Heading { get; }
    public string Rule { get; }
    public ProjectFact<string> Evidence { get; }
    public IReadOnlyList<FactSource> Sources { get; }

    public ProjectConvention(string documentPath, string heading, string rule, ProjectFact<string> evidence,
        IReadOnlyList<FactSource>? sources = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(heading);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);
        ArgumentNullException.ThrowIfNull(evidence);
        DocumentPath = documentPath;
        Heading = heading;
        Rule = rule;
        Evidence = evidence;
        // Missing, null and empty collections retain the primary source from older snapshots.
        var sourceArray = sources is null || sources.Count == 0 ? [evidence.Source] : sources.ToArray();
        if (sourceArray.Any(source => source is null))
            throw new ArgumentException("Convention sources cannot contain null entries.", nameof(sources));
        Sources = Array.AsReadOnly(sourceArray);
    }

    public bool Equals(ProjectConvention? other) => other is not null &&
        DocumentPath == other.DocumentPath && Heading == other.Heading && Rule == other.Rule &&
        Evidence == other.Evidence && Sources.SequenceEqual(other.Sources);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(DocumentPath);
        hash.Add(Heading);
        hash.Add(Rule);
        hash.Add(Evidence);
        foreach (var source in Sources)
            hash.Add(source);
        return hash.ToHashCode();
    }
}
